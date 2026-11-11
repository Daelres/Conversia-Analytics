using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Ingestion;

public class WebhookEventProcessor : IWebhookEventProcessor
{
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IContactRepository _contacts;
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly TimeProvider _timeProvider;
    private readonly WebhookIngestionLimits _limits;
    private readonly ILogger<WebhookEventProcessor> _logger;

    public WebhookEventProcessor(
        IWhatsAppChannelRepository channels, IContactRepository contacts, IConversationRepository conversations,
        IMessageRepository messages, TimeProvider timeProvider, WebhookIngestionLimits limits, ILogger<WebhookEventProcessor> logger)
    {
        _logger = logger;
        _limits = limits;
        _channels = channels;
        _contacts = contacts;
        _conversations = conversations;
        _messages = messages;
        _timeProvider = timeProvider;
    }

    public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        // A channel that cannot be found here would be a deleted or foreign one: fail (and retry), never guess.
        var channel = await _channels.GetByIdAsync(webhookEvent.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the webhook event no longer exists.");

        var parsed = Dialog360PayloadParser.Parse(webhookEvent.RawPayload, _timeProvider.GetUtcNow().UtcDateTime);

        // Bounded work per delivery: a bigger one would hold the tenant's queue (and a transaction) for too long, and no retry changes its size.
        if (parsed.Messages.Count + parsed.SkippedCount > _limits.MaxMessagesPerDelivery)
        {
            throw new WebhookPermanentFailureException(
                $"The delivery holds {parsed.Messages.Count + parsed.SkippedCount} messages, more than the {_limits.MaxMessagesPerDelivery} allowed per delivery.");
        }

        var crossedTheWall = new HashSet<Guid>(); // contacts whose messages were refused because their mark is not the delivery's
        var stored = 0;
        var alreadyStored = 0;
        var skipped = parsed.SkippedCount;
        var contacts = new Dictionary<string, Contact>();
        var conversations = new Dictionary<Guid, Conversation>(); // by contact: one open conversation per contact on this channel

        // Oldest first, so the message that opens a conversation is the first one in time. Arrival order never decides anything
        // that matters: the conversation's markers are recomputed from the stored message times once all its messages are in.
        foreach (var message in parsed.Messages.OrderBy(m => m.SentAt).ThenBy(m => m.ProviderMessageId, StringComparer.Ordinal))
        {
            if (IsAnotherNumber(message, channel))
            {
                skipped++;
            }
            else if (await _messages.ExistsAsync(channel.Id, message.ProviderMessageId, cancellationToken))
            {
                alreadyStored++;
            }
            else if (await StoreAsync(channel, message, webhookEvent.IsSandbox, contacts, conversations, cancellationToken))
            {
                stored++;
            }
            else
            {
                skipped++;
                crossedTheWall.Add(contacts[message.ContactWaId].Id);
            }
        }

        // One timeline per conversation, after its messages: not one reload of the whole history per message. Every conversation that
        // received a message is also queued for classification (the worker reads it once the conversation has been quiet for a while).
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var conversation in conversations.Values)
        {
            var times = await _messages.ListTimesAsync(conversation.Id, cancellationToken);
            conversation.ApplyTimeline(ConversationTimeline.From(times)!);
            conversation.RequestClassification(now);
            await _conversations.UpdateAsync(conversation, cancellationToken);
        }

        if (crossedTheWall.Count > 0)
        {
            // It should never happen (the simulated customer's numbers are made up by the API, and no real customer can have one): somebody
            // must look. Ids only: no number, no name, no text.
            _logger.LogWarning(
                "Webhook event {EventId} of tenant {TenantId} on channel {ChannelId} (sandbox delivery: {IsSandbox}) carried messages for contacts of the other kind, which were not stored: {ContactIds}.",
                webhookEvent.Id, webhookEvent.TenantId, webhookEvent.ChannelId, webhookEvent.IsSandbox, string.Join(", ", crossedTheWall));
        }

        return new WebhookProcessingResult(stored, alreadyStored, skipped, parsed.IgnoredCount, parsed.StatusCount);
    }

    // A delivery that names a different business number than the channel's is a misconfigured webhook; its messages are not ours.
    private static bool IsAnotherNumber(ParsedMessage message, WhatsAppChannel channel)
        => channel.ProviderPhoneNumberId is not null
           && message.PhoneNumberId is not null
           && !string.Equals(channel.ProviderPhoneNumberId, message.PhoneNumberId, StringComparison.Ordinal);

    // False when the message was not stored because it would cross the sandbox boundary (docs/SANDBOX.md): a sandbox delivery never
    // touches a real contact, and a real delivery never touches a simulated one. The mark comes from the delivery, never from the number.
    private async Task<bool> StoreAsync(
        WhatsAppChannel channel, ParsedMessage message, bool isSandbox, Dictionary<string, Contact> contacts,
        Dictionary<Guid, Conversation> conversations, CancellationToken cancellationToken)
    {
        var contact = await GetOrCreateContactAsync(channel.TenantId, message, isSandbox, contacts, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var conversation = await GetOrCreateConversationAsync(channel, contact, message, conversations, cancellationToken);

        await _messages.AddAsync(
            Message.Create(channel.TenantId, conversation.Id, channel.Id, message.ProviderMessageId, message.Direction, message.Type, message.Text, message.SentAt),
            cancellationToken);

        if (message.Referral is not null)
        {
            conversation.MarkAsAds(message.Referral.Reference);
        }

        return true;
    }

    private async Task<Contact?> GetOrCreateContactAsync(
        Guid tenantId, ParsedMessage message, bool isSandbox, Dictionary<string, Contact> contacts, CancellationToken cancellationToken)
    {
        if (contacts.TryGetValue(message.ContactWaId, out var known))
        {
            if (known.IsSandbox != isSandbox)
            {
                return null;
            }

            known.ObserveAt(message.SentAt);
            known.UpdateDisplayName(message.ContactName, message.SentAt);
            return known;
        }

        var contact = await _contacts.GetByWaIdAsync(message.ContactWaId, cancellationToken);
        if (contact is null)
        {
            contact = Contact.Create(tenantId, message.ContactWaId, message.ContactName, message.SentAt, isSandbox);
            await _contacts.AddAsync(contact, cancellationToken);
        }
        else if (contact.IsSandbox != isSandbox)
        {
            // Remembered, so the other messages of this contact in the delivery are refused without another lookup; nothing of it is changed.
            contacts[message.ContactWaId] = contact;
            return null;
        }
        else
        {
            contact.ObserveAt(message.SentAt);
            contact.UpdateDisplayName(message.ContactName, message.SentAt);
            await _contacts.UpdateAsync(contact, cancellationToken);
        }

        contacts[message.ContactWaId] = contact;
        return contact;
    }

    // A conversation is sandbox data exactly when its contact is: both marks come from the delivery that created the contact.
    // Reuse the contact's open conversation on this channel, otherwise open one (with its opportunity) at the time of the message.
    private async Task<Conversation> GetOrCreateConversationAsync(
        WhatsAppChannel channel, Contact contact, ParsedMessage message, Dictionary<Guid, Conversation> conversations, CancellationToken cancellationToken)
    {
        if (conversations.TryGetValue(contact.Id, out var known))
        {
            return known;
        }

        var conversation = await _conversations.FindOpenAsync(contact.Id, channel.Id, cancellationToken);
        if (conversation is null)
        {
            conversation = Conversation.Start(
                channel.TenantId, contact.Id, channel.Id, channel.BranchId, message.SentAt,
                message.Referral is null ? ConversationSource.Organic : ConversationSource.Ads,
                message.Referral?.Reference,
                contact.IsSandbox);
            await _conversations.AddAsync(conversation, Opportunity.Open(channel.TenantId, conversation.Id), cancellationToken);
        }

        conversations[contact.Id] = conversation;
        return conversation;
    }
}
