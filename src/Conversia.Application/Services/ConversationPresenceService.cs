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
/// Read receipts and the typing indicator (docs/MESSAGING.md, "Read receipts and typing"). Both change what the customer sees, so both
/// need the company's setting; both go through the same sender resolver as a message (the internal sender in sandbox mode). Nothing here
/// ever blocks the advisor: every failure is logged (ids only) and answered as <see cref="PresenceOutcome.Failed"/>.
/// </summary>
public sealed class ConversationPresenceService(
    IConversationRepository conversations, IMessageRepository messages, IWhatsAppChannelRepository channels, ITenantRepository tenants,
    IWhatsAppMessageSenderResolver senders, TypingIndicatorThrottle throttle, TimeProvider timeProvider, ILogger<ConversationPresenceService> logger)
    : IConversationPresenceService
{
    public async Task<PresenceResponse> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        if (!await tenants.GetReadReceiptsEnabledAsync(conversation.TenantId, cancellationToken))
        {
            return Answer(PresenceOutcome.Disabled);
        }

        if (!conversation.NeedsReadReceipt)
        {
            return Answer(PresenceOutcome.NotNeeded);
        }

        if (throttle.ReadCoolingDown(conversation.Id))
        {
            return Answer(PresenceOutcome.Throttled);
        }

        var outcome = await SendReceiptAsync(conversation, typing: false, cancellationToken);
        if (outcome == PresenceOutcome.Failed)
        {
            throttle.ReadFailed(conversation.Id);
        }

        return Answer(outcome);
    }

    public async Task<PresenceResponse> SendTypingAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        if (!await tenants.GetReadReceiptsEnabledAsync(conversation.TenantId, cancellationToken))
        {
            return Answer(PresenceOutcome.Disabled);
        }

        // Only when a reply is plausible: the conversation is open, the customer wrote last, and free text can still be sent.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (conversation.Status != ConversationStatus.Open || !conversation.AwaitingReply || !CustomerServiceWindow.IsOpen(conversation.LastInboundAt, now))
        {
            return Answer(PresenceOutcome.NotNeeded);
        }

        if (!throttle.TryAcquire(conversation.Id))
        {
            return Answer(PresenceOutcome.Throttled);
        }

        return Answer(await SendReceiptAsync(conversation, typing: true, cancellationToken));
    }

    private static PresenceResponse Answer(string outcome) => new() { Outcome = outcome };

    private async Task<string> SendReceiptAsync(Conversation conversation, bool typing, CancellationToken cancellationToken)
    {
        try
        {
            // The sandbox boundary of a send (docs/SANDBOX.md): nothing about a real customer in sandbox mode, nothing about a simulated one outside it.
            var sandboxMode = await tenants.GetSandboxModeAsync(conversation.TenantId, cancellationToken) ?? false;
            if (conversation.IsSandbox != sandboxMode)
            {
                return Failed(conversation, typing, "sandbox boundary");
            }

            var channel = await channels.GetByIdAsync(conversation.ChannelId, cancellationToken);
            if (channel is not { IsActive: true } || (!sandboxMode && !channel.HasProviderApiKey))
            {
                return Failed(conversation, typing, "channel inactive or without a key");
            }

            if (await messages.GetLastInboundAsync(conversation.Id, cancellationToken) is not { } last)
            {
                return PresenceOutcome.NotNeeded;
            }

            var status = await senders.Resolve(channel, sandboxMode).MarkReadAsync(new WhatsAppReadReceipt(channel, last.ProviderMessageId, typing), cancellationToken);
            if (status != WhatsAppSendStatus.Sent)
            {
                return Failed(conversation, typing, status.ToString());
            }

            // Both mark the message as read on WhatsApp; remembering it saves the next opening a request.
            await conversations.ChangeSerializedWithIngestionAsync(
                conversation.TenantId, conversation.Id, locked =>
                {
                    locked.MarkCustomerMessagesRead(last.SentAt);
                    return Task.CompletedTask;
                },
                cancellationToken);
            conversation.MarkCustomerMessagesRead(last.SentAt);

            if (sandboxMode)
            {
                logger.LogInformation(
                    "Sandbox {Kind} for conversation {ConversationId}: accepted by the internal sender, nothing reached a phone.",
                    typing ? "typing indicator" : "read receipt", conversation.Id);
                return PresenceOutcome.Sandbox;
            }

            return PresenceOutcome.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(conversation, typing, ex.GetType().Name);
        }
    }

    private string Failed(Conversation conversation, bool typing, string reason)
    {
        logger.LogWarning(
            "The {Kind} for conversation {ConversationId} was not sent ({Reason}).", typing ? "typing indicator" : "read receipt", conversation.Id, reason);
        return PresenceOutcome.Failed;
    }
}
