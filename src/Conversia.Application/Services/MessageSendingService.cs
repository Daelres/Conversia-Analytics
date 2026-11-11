using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Services;

/// <summary>
/// Sends one text message in three steps: check (plain reads, no lock), call the provider (no transaction open), record. Only the last
/// step locks anything, and it is the step that stores the message through the same domain paths as ingestion.
/// </summary>
public class MessageSendingService : IMessageSendingService
{
    /// <summary>Type of a plain-text message, as the provider names it.</summary>
    public const string TextType = "text";

    /// <summary>
    /// Prefix of the id given to a sent message whose provider answer carried no id. Such a message cannot be matched with its echo,
    /// should the provider ever deliver one.
    /// </summary>
    public const string LocalMessageIdPrefix = "conversia.";

    private readonly IConversationRepository _conversations;
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IContactRepository _contacts;
    private readonly IMessageRepository _messages;
    private readonly ITenantRepository _tenants;
    private readonly IWhatsAppMessageSenderResolver _senders;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MessageSendingService> _logger;

    public MessageSendingService(
        IConversationRepository conversations, IWhatsAppChannelRepository channels, IContactRepository contacts, IMessageRepository messages,
        ITenantRepository tenants, IWhatsAppMessageSenderResolver senders, TimeProvider timeProvider, ILogger<MessageSendingService> logger)
    {
        _logger = logger;
        _conversations = conversations;
        _channels = channels;
        _contacts = contacts;
        _messages = messages;
        _tenants = tenants;
        _senders = senders;
        _timeProvider = timeProvider;
    }

    public async Task<MessageResponse> SendTextAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        var text = TextRules.Required(request.Text, Message.TextMaxLength, SendMessageRequest.TextRuleMessage);

        // Untracked reads: nothing is locked, and no transaction is open, while the provider is being called.
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        if (conversation.Status == ConversationStatus.Closed)
        {
            throw new MessagingException(MessagingProblem.ConversationClosed);
        }

        // The internal sandbox (docs/SANDBOX.md) and the real data never mix: a sandbox conversation takes messages only in sandbox mode
        // (and then nothing reaches the provider), a real one only outside it.
        var sandboxMode = await _tenants.GetSandboxModeAsync(conversation.TenantId, cancellationToken) ?? false;
        if (conversation.IsSandbox != sandboxMode)
        {
            throw new MessagingException(conversation.IsSandbox ? MessagingProblem.SandboxConversation : MessagingProblem.SandboxModeActive);
        }

        // A conversation always has its channel and its contact (foreign keys, same tenant); a missing one would be a defect, not a 404.
        var channel = await _channels.GetByIdAsync(conversation.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the conversation no longer exists.");
        if (!channel.IsActive)
        {
            throw new MessagingException(MessagingProblem.ChannelInactive);
        }

        // The internal sender calls no provider, so it needs no key.
        if (!sandboxMode && !channel.HasProviderApiKey)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        var contact = await _contacts.GetByIdAsync(conversation.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("The contact of the conversation no longer exists.");

        var result = await _senders.Resolve(channel, sandboxMode).SendTextAsync(new WhatsAppTextMessage(channel, contact.WaId, text), cancellationToken);
        if (result.Status != WhatsAppSendStatus.Sent)
        {
            throw new MessagingException(ProblemOf(result.Status));
        }

        var sentAt = _timeProvider.GetUtcNow().UtcDateTime;
        var providerMessageId = result.ProviderMessageId ?? $"{LocalMessageIdPrefix}{Guid.NewGuid():N}";

        try
        {
            // The customer already has the message: the record must not be abandoned because the caller went away.
            return await RecordAsync(conversation, providerMessageId, text, sentAt);
        }
        catch (Exception ex) when (ex is not MessagingException)
        {
            // In sandbox mode nothing left the server. If the record failed because the company deleted its sandbox data meanwhile (the
            // conversation is gone, whether the lookup under the lock found no row or the insert hit its foreign key), there is no message
            // anywhere to verify or to keep: that is a conflict to tell the person about, not "sent but not recorded".
            if (sandboxMode && (ex is ConversationGoneException || await ConversationIsGoneAsync(conversation.Id)))
            {
                throw new MessagingException(MessagingProblem.SandboxDataDeleted, ex);
            }

            throw new MessagingException(MessagingProblem.SentNotRecorded, ex);
        }
    }

    /// <summary>The conversation row was not there any more when the record step locked it.</summary>
    private sealed class ConversationGoneException() : InvalidOperationException("The conversation disappeared while its sent message was being recorded.");

    // Never the reason to hide the original failure: a lookup that fails too leaves the answer as "sent but not recorded".
    private async Task<bool> ConversationIsGoneAsync(Guid conversationId)
    {
        try
        {
            return await _conversations.GetByIdAsync(conversationId, CancellationToken.None) is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The same writes ingestion makes for a message it stores (WebhookEventProcessor), under the same lock, so the two never interleave:
    // if the provider echoes this message to the webhook, whichever comes second finds it stored and adds nothing.
    private async Task<MessageResponse> RecordAsync(Conversation read, string providerMessageId, string text, DateTime sentAt)
    {
        Message? recorded = null;
        var found = await _conversations.ChangeSerializedWithIngestionAsync(
            read.TenantId,
            read.Id,
            async conversation =>
            {
                await WarnIfChangedSinceTheCheckAsync(conversation);
                recorded = await _messages.FindAsync(conversation.ChannelId, providerMessageId, CancellationToken.None);
                if (recorded is null)
                {
                    recorded = Message.Create(
                        conversation.TenantId, conversation.Id, conversation.ChannelId, providerMessageId, MessageDirection.Outbound, TextType, text, sentAt);
                    await _messages.AddAsync(recorded, CancellationToken.None);
                }

                var times = await _messages.ListTimesAsync(conversation.Id, CancellationToken.None);
                conversation.ApplyTimeline(ConversationTimeline.From(times)!);
                conversation.RequestClassification(sentAt);
            },
            CancellationToken.None);

        if (!found || recorded is null)
        {
            throw new ConversationGoneException();
        }

        return MessageResponse.From(recorded);
    }

    // The check ran before the provider was called and holds no lock, so the conversation may have been closed or its channel switched
    // off in between. The customer has the message either way: it is stored, and the odd state is left in the log (ids only).
    private async Task WarnIfChangedSinceTheCheckAsync(Conversation conversation)
    {
        var closed = conversation.Status == ConversationStatus.Closed;
        var channel = await _channels.GetByIdAsync(conversation.ChannelId, CancellationToken.None);
        var channelOff = channel is not { IsActive: true };
        if (closed || channelOff)
        {
            _logger.LogWarning(
                "A sent message is being recorded in conversation {ConversationId} of channel {ChannelId} although it changed while the provider was called (conversation closed: {Closed}; channel inactive: {ChannelInactive}).",
                conversation.Id, conversation.ChannelId, closed, channelOff);
        }
    }

    private static MessagingProblem ProblemOf(WhatsAppSendStatus status) => status switch
    {
        WhatsAppSendStatus.InvalidCredentials => MessagingProblem.InvalidCredentials,
        WhatsAppSendStatus.RecipientNotAllowed => MessagingProblem.RecipientNotAllowed,
        WhatsAppSendStatus.LimitReached => MessagingProblem.LimitReached,
        WhatsAppSendStatus.Rejected => MessagingProblem.Rejected,
        WhatsAppSendStatus.TransientFailure => MessagingProblem.ProviderUnavailable,
        _ => throw new InvalidOperationException($"Unexpected send status {status}."),
    };
}
