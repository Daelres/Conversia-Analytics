using System.Text;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Sandbox;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Services;

/// <summary>
/// The simulated customer of the internal sandbox. It does not store messages: it builds the delivery the provider would have sent and
/// hands it to <see cref="IWebhookIngestionService.StoreAsync"/>, marked as sandbox, so the queue, the worker, the parser, the
/// de-duplication, the classification request and the close-by-inactivity are the real ones.
/// </summary>
public class SandboxService : ISandboxService
{
    /// <summary>What the customer is called when the contact of the conversation has no name.</summary>
    private const string FallbackCustomerName = "Cliente de prueba";

    private const int MaxNumberAttempts = 10;

    private readonly ITenantRepository _tenants;
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IConversationRepository _conversations;
    private readonly IContactRepository _contacts;
    private readonly IWebhookIngestionService _ingestion;
    private readonly ISandboxDataStore _store;
    private readonly ISandboxCustomerNumbers _numbers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SandboxService> _logger;

    public SandboxService(
        ITenantRepository tenants, IWhatsAppChannelRepository channels, IConversationRepository conversations, IContactRepository contacts,
        IWebhookIngestionService ingestion, ISandboxDataStore store, ISandboxCustomerNumbers numbers, TimeProvider timeProvider, ILogger<SandboxService> logger)
    {
        _numbers = numbers;
        _tenants = tenants;
        _channels = channels;
        _conversations = conversations;
        _contacts = contacts;
        _ingestion = ingestion;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SandboxMessageAcceptedResponse> StartConversationAsync(
        Guid tenantId, StartSandboxConversationRequest request, CancellationToken cancellationToken = default)
    {
        var name = TextRules.Required(request.CustomerName, Contact.DisplayNameMaxLength, StartSandboxConversationRequest.CustomerNameRuleMessage);
        var text = TextRules.Required(request.Text, Message.TextMaxLength, StartSandboxConversationRequest.TextRuleMessage);
        await RequireSandboxModeAsync(tenantId, cancellationToken);

        // Tenant-filtered: a channel of another company is as unknown as one that does not exist.
        var channel = await _channels.GetByIdAsync(request.ChannelId ?? Guid.Empty, cancellationToken) ?? throw new ChannelNotFoundException();
        RequireActive(channel);

        var waId = await NewCustomerNumberAsync(cancellationToken);
        return await DeliverAsync(channel, waId, name, text, notBefore: null, cancellationToken);
    }

    public async Task<SandboxMessageAcceptedResponse> SendCustomerMessageAsync(
        Guid tenantId, Guid conversationId, SandboxCustomerMessageRequest request, CancellationToken cancellationToken = default)
    {
        var text = TextRules.Required(request.Text, Message.TextMaxLength, StartSandboxConversationRequest.TextRuleMessage);
        await RequireSandboxModeAsync(tenantId, cancellationToken);

        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        if (!conversation.IsSandbox)
        {
            throw new SandboxException(SandboxProblem.NotSandboxConversation);
        }

        if (conversation.Status == ConversationStatus.Closed)
        {
            throw new SandboxException(SandboxProblem.ConversationClosed);
        }

        var channel = await _channels.GetByIdAsync(conversation.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the conversation no longer exists.");
        RequireActive(channel);

        var contact = await _contacts.GetByIdAsync(conversation.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("The contact of the conversation no longer exists.");

        return await DeliverAsync(channel, contact.WaId, contact.DisplayName ?? FallbackCustomerName, text, conversation.LastMessageAt, cancellationToken);
    }

    public async Task<SandboxDataDeletedResponse> DeleteDataAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var counts = await _store.DeleteAllAsync(tenantId, cancellationToken);

        // The trail of a destructive action taken by a company's user: ids and counts only (the platform audit log is the administrators').
        _logger.LogInformation(
            "Sandbox data of tenant {TenantId} deleted by user {UserId}: {Conversations} conversations, {Messages} messages, {Contacts} contacts, {WebhookEvents} webhook events.",
            tenantId, userId, counts.Conversations, counts.Messages, counts.Contacts, counts.WebhookEvents);

        return new SandboxDataDeletedResponse
        {
            Conversations = counts.Conversations,
            Messages = counts.Messages,
            Contacts = counts.Contacts,
            WebhookEvents = counts.WebhookEvents,
        };
    }

    private async Task RequireSandboxModeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await _tenants.GetSandboxModeAsync(tenantId, cancellationToken) != true)
        {
            throw new SandboxException(SandboxProblem.ModeOff);
        }
    }

    private static void RequireActive(WhatsAppChannel channel)
    {
        if (!channel.IsActive)
        {
            throw new SandboxException(SandboxProblem.ChannelInactive);
        }
    }

    // A number of the sandbox range that the company does not have yet, so a new simulated customer never lands in somebody else's conversation.
    private async Task<string> NewCustomerNumberAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxNumberAttempts; attempt++)
        {
            var waId = _numbers.Next();
            if (await _contacts.GetByWaIdAsync(waId, cancellationToken) is null)
            {
                return waId;
            }
        }

        throw new InvalidOperationException("No free sandbox customer number was found.");
    }

    private async Task<SandboxMessageAcceptedResponse> DeliverAsync(
        WhatsAppChannel channel, string customerWaId, string customerName, string text, DateTime? notBefore, CancellationToken cancellationToken)
    {
        var sentAt = WholeSecond(_timeProvider.GetUtcNow().UtcDateTime, notBefore);
        var body = SandboxWebhookPayload.InboundText(channel, customerWaId, customerName, SandboxIds.NewInboundMessageId(), text, sentAt);

        var result = await _ingestion.StoreAsync(
            new WebhookChannelAccess(channel.Id, channel.TenantId, IsSandbox: true), Encoding.UTF8.GetBytes(body), cancellationToken);
        if (result != WebhookStoreResult.Stored)
        {
            // Every body carries a fresh message id and validated text, so anything but Stored is a defect, not the caller's mistake.
            throw new InvalidOperationException($"The sandbox delivery was not stored ({result}).");
        }

        return new SandboxMessageAcceptedResponse { CustomerWaId = customerWaId, SentAt = sentAt };
    }

    // A delivery carries whole seconds, like the provider's. A reply written within the same second as the last message of the
    // conversation (an advisor's answer has fractions) would be cut to before it and read out of order, so it takes the next second.
    private static DateTime WholeSecond(DateTime now, DateTime? notBefore)
    {
        var second = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        if (notBefore is { } last && second <= last)
        {
            second = new DateTime(last.Ticks - (last.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddSeconds(1);
        }

        return second;
    }
}
