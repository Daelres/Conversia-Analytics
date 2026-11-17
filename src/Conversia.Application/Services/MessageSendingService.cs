using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Sandbox;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
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

    /// <summary>Type of a template message, as the provider names it.</summary>
    public const string TemplateType = "template";

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
    private readonly IMessageSendOutcomeRecorder? _outcomes;
    private readonly IAdvisorRepository? _advisors;
    private readonly IMessageTemplateRepository? _templates;

    /// <param name="outcomes">Counts how each send ended, for the platform's monitoring (docs/PLATFORM_ADMIN.md, "Monitoring").</param>
    public MessageSendingService(
        IConversationRepository conversations, IWhatsAppChannelRepository channels, IContactRepository contacts, IMessageRepository messages,
        ITenantRepository tenants, IWhatsAppMessageSenderResolver senders, TimeProvider timeProvider, ILogger<MessageSendingService> logger,
        IMessageSendOutcomeRecorder? outcomes = null, IAdvisorRepository? advisors = null, IMessageTemplateRepository? templates = null)
    {
        _templates = templates;
        _advisors = advisors;
        _logger = logger;
        _outcomes = outcomes;
        _conversations = conversations;
        _channels = channels;
        _contacts = contacts;
        _messages = messages;
        _tenants = tenants;
        _senders = senders;
        _timeProvider = timeProvider;
    }

    public async Task<MessageResponse> SendTextAsync(Guid conversationId, SendMessageRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default)
    {
        var text = TextRules.Required(request.Text, Message.TextMaxLength, SendMessageRequest.TextRuleMessage);
        return await CountedAsync(conversationId, actingUserId, new TextPayload(text), cancellationToken);
    }

    public async Task<MessageResponse> SendTemplateAsync(
        Guid conversationId, SendTemplateMessageRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default)
    {
        var name = request.TemplateName?.Trim() ?? string.Empty;
        var language = request.Language?.Trim() ?? string.Empty;
        return await CountedAsync(conversationId, actingUserId, new TemplatePayload(name, language, request.Parameters ?? []), cancellationToken);
    }

    /// <summary>What a send carries: a free-form text, or a template with its values. Everything around it is the same path.</summary>
    private abstract record Payload;

    private sealed record TextPayload(string Text) : Payload;

    private sealed record TemplatePayload(string Name, string Language, IReadOnlyList<string?> Values) : Payload;

    private async Task<MessageResponse> CountedAsync(Guid conversationId, Guid? actingUserId, Payload payload, CancellationToken cancellationToken)
    {
        // Untracked reads: nothing is locked, and no transaction is open, while the provider is being called.
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        string? outcome = MessageSendOutcome.Unexpected;
        try
        {
            var response = await SendAsync(conversation, payload, actingUserId, cancellationToken);
            outcome = MessageSendOutcome.Sent;
            return response;
        }
        catch (MessagingException ex)
        {
            outcome = ex.Code;
            throw;
        }
        catch (DomainException)
        {
            outcome = null; // values that do not fit the template: a request that was not valid, not a send
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = null; // the caller went away before anything was decided: not an outcome of the send
            throw;
        }
        finally
        {
            if (_outcomes is not null && outcome is not null)
            {
                await CountAsync(conversation, outcome);
            }
        }
    }

    // The customer's fate is decided; the counter must neither depend on the caller still waiting nor change the answer when it fails.
    private async Task CountAsync(Conversation conversation, string outcome)
    {
        try
        {
            await _outcomes!.RecordAsync(conversation.TenantId, conversation.IsSandbox, outcome, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The outcome {Outcome} of a send in conversation {ConversationId} could not be counted ({ErrorType}).", outcome, conversation.Id, ex.GetType().Name);
        }
    }

    private async Task<MessageResponse> SendAsync(Conversation conversation, Payload payload, Guid? actingUserId, CancellationToken cancellationToken)
    {
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

        // WhatsApp delivers free-form text only within 24 hours of the customer's last message (docs/MESSAGING.md, "Customer service
        // window"); outside it the provider refuses it anyway (and a sandbox key counts the refusal), so it is refused here first. The same
        // rule in sandbox mode, so that what a person practices there is what happens for real. A template is what the window allows.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (payload is TextPayload && !CustomerServiceWindow.IsOpen(conversation.LastInboundAt, now))
        {
            throw new MessagingException(MessagingProblem.WindowClosed);
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

        // Who sends it (docs/MESSAGING.md, "Who sent it"): the user, and the advisor linked to that user. Never the conversation's assigned
        // advisor: a manager writing in someone else's conversation is not that advisor.
        var advisorId = actingUserId is { } userId && _advisors is not null
            ? (await _advisors.GetByUserIdAsync(userId, cancellationToken))?.Id
            : null;

        var sender = _senders.Resolve(channel, sandboxMode);
        WhatsAppSendResult result;
        string text;
        string type;
        MessageTemplateUse? templateUse = null;
        switch (payload)
        {
            case TextPayload plain:
                text = plain.Text;
                type = TextType;
                result = await sender.SendTextAsync(new WhatsAppTextMessage(channel, contact.WaId, text), cancellationToken);
                break;
            case TemplatePayload templated:
                var template = await FindTemplateAsync(channel, templated.Name, templated.Language, sandboxMode, now, cancellationToken);
                var values = template.ValidateParameters(templated.Values);
                text = template.Render(values);
                type = TemplateType;
                templateUse = new MessageTemplateUse(template.Name, template.Category);
                var parameters = template.ParameterNames
                    .Select((placeholder, i) => new WhatsAppTemplateParameter(template.NamedParameters ? placeholder : null, values[i]))
                    .ToList();
                result = await sender.SendTemplateAsync(new WhatsAppTemplateMessage(channel, contact.WaId, template.Name, template.Language, parameters), cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unexpected payload {payload.GetType().Name}.");
        }

        if (result.Status != WhatsAppSendStatus.Sent)
        {
            throw new MessagingException(ProblemOf(result.Status));
        }

        var sentAt = _timeProvider.GetUtcNow().UtcDateTime;
        var providerMessageId = result.ProviderMessageId ?? $"{LocalMessageIdPrefix}{Guid.NewGuid():N}";

        try
        {
            // The customer already has the message: the record must not be abandoned because the caller went away.
            return await RecordAsync(conversation, providerMessageId, type, text, sentAt, actingUserId, advisorId, templateUse);
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
    private async Task<MessageResponse> RecordAsync(
        Conversation read, string providerMessageId, string type, string text, DateTime sentAt, Guid? userId, Guid? advisorId, MessageTemplateUse? template)
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
                        conversation.TenantId, conversation.Id, conversation.ChannelId, providerMessageId, MessageDirection.Outbound, type, text, sentAt,
                        MessageSender.Conversia, userId, advisorId, template: template);
                    await _messages.AddAsync(recorded, CancellationToken.None);
                }
                else if (recorded.Direction == MessageDirection.Outbound && recorded.Sender != MessageSender.Conversia)
                {
                    // Its echo arrived first and was stored as sent from the phone app: it was sent from here.
                    recorded.AttributeToConversia(userId, advisorId);
                    await _messages.UpdateAsync(recorded, CancellationToken.None);
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

    // In sandbox mode only the fixed sandbox list exists (docs/SANDBOX.md); otherwise the channel's synced copy. Either way the template
    // must be approved and something Conversia can send.
    private async Task<MessageTemplate> FindTemplateAsync(
        WhatsAppChannel channel, string name, string language, bool sandboxMode, DateTime now, CancellationToken cancellationToken)
    {
        var template = sandboxMode
            ? SandboxTemplates.Find(channel, name, language, now)
            : _templates is null || !MessageTemplate.IsValidName(name) || !MessageTemplate.IsValidLanguage(language)
                ? null
                : await _templates.FindAsync(channel.Id, name, language, cancellationToken);

        return template is { CanBeSent: true } ? template : throw new MessagingException(MessagingProblem.TemplateUnavailable);
    }

    private static MessagingProblem ProblemOf(WhatsAppSendStatus status) => status switch
    {
        WhatsAppSendStatus.InvalidCredentials => MessagingProblem.InvalidCredentials,
        WhatsAppSendStatus.RecipientNotAllowed => MessagingProblem.RecipientNotAllowed,
        WhatsAppSendStatus.LimitReached => MessagingProblem.LimitReached,
        WhatsAppSendStatus.Rejected => MessagingProblem.Rejected,
        WhatsAppSendStatus.TransientFailure => MessagingProblem.ProviderUnavailable,
        WhatsAppSendStatus.WindowClosed => MessagingProblem.WindowClosed,
        _ => throw new InvalidOperationException($"Unexpected send status {status}."),
    };
}
