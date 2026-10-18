using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Ingestion;

public class WebhookEventProcessor : IWebhookEventProcessor
{
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IContactRepository _contacts;
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly TimeProvider _timeProvider;
    private readonly WebhookIngestionLimits _limits;

    public WebhookEventProcessor(
        IWhatsAppChannelRepository channels, IContactRepository contacts, IConversationRepository conversations,
        IMessageRepository messages, TimeProvider timeProvider, WebhookIngestionLimits limits)
    {
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
            else
            {
                await StoreAsync(channel, message, contacts, conversations, cancellationToken);
                stored++;
            }
        }

        // One timeline per conversation, after its messages: not one reload of the whole history per message.
        foreach (var conversation in conversations.Values)
        {
            var times = await _messages.ListTimesAsync(conversation.Id, cancellationToken);
            conversation.ApplyTimeline(ConversationTimeline.From(times)!);
            await _conversations.UpdateAsync(conversation, cancellationToken);
        }

        return new WebhookProcessingResult(stored, alreadyStored, skipped, parsed.IgnoredCount, parsed.StatusCount);
    }

    // A delivery that names a different business number than the channel's is a misconfigured webhook; its messages are not ours.
    private static bool IsAnotherNumber(ParsedMessage message, WhatsAppChannel channel)
        => channel.ProviderPhoneNumberId is not null
           && message.PhoneNumberId is not null
           && !string.Equals(channel.ProviderPhoneNumberId, message.PhoneNumberId, StringComparison.Ordinal);

    private async Task StoreAsync(
        WhatsAppChannel channel, ParsedMessage message, Dictionary<string, Contact> contacts,
        Dictionary<Guid, Conversation> conversations, CancellationToken cancellationToken)
    {
        var contact = await GetOrCreateContactAsync(channel.TenantId, message, contacts, cancellationToken);
        var conversation = await GetOrCreateConversationAsync(channel, contact, message, conversations, cancellationToken);

        await _messages.AddAsync(
            Message.Create(channel.TenantId, conversation.Id, channel.Id, message.ProviderMessageId, message.Direction, message.Type, message.Text, message.SentAt),
            cancellationToken);

        if (message.Referral is not null)
        {
            conversation.MarkAsAds(message.Referral.Reference);
        }
    }

    private async Task<Contact> GetOrCreateContactAsync(
        Guid tenantId, ParsedMessage message, Dictionary<string, Contact> contacts, CancellationToken cancellationToken)
    {
        if (contacts.TryGetValue(message.ContactWaId, out var known))
        {
            known.ObserveAt(message.SentAt);
            known.UpdateDisplayName(message.ContactName, message.SentAt);
            return known;
        }

        var contact = await _contacts.GetByWaIdAsync(message.ContactWaId, cancellationToken);
        if (contact is null)
        {
            contact = Contact.Create(tenantId, message.ContactWaId, message.ContactName, message.SentAt);
            await _contacts.AddAsync(contact, cancellationToken);
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
                message.Referral?.Reference);
            await _conversations.AddAsync(conversation, Opportunity.Open(channel.TenantId, conversation.Id), cancellationToken);
        }

        conversations[contact.Id] = conversation;
        return conversation;
    }
}
