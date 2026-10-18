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

    public WebhookEventProcessor(
        IWhatsAppChannelRepository channels, IContactRepository contacts, IConversationRepository conversations, IMessageRepository messages)
    {
        _channels = channels;
        _contacts = contacts;
        _conversations = conversations;
        _messages = messages;
    }

    public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        // A channel that cannot be found here would be a deleted or foreign one: fail (and retry), never guess.
        var channel = await _channels.GetByIdAsync(webhookEvent.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the webhook event no longer exists.");

        var parsed = Dialog360PayloadParser.Parse(webhookEvent.RawPayload);

        var stored = 0;
        var alreadyStored = 0;
        var ignored = parsed.StatusCount + parsed.SkippedCount;

        // Oldest first, so the message that opens a conversation is the first one in time. Arrival order never decides anything
        // that matters: the conversation's markers are recomputed from the stored message times after every message.
        foreach (var message in parsed.Messages.OrderBy(m => m.SentAt).ThenBy(m => m.ProviderMessageId, StringComparer.Ordinal))
        {
            if (IsAnotherNumber(message, channel))
            {
                ignored++;
            }
            else if (await _messages.ExistsAsync(channel.Id, message.ProviderMessageId, cancellationToken))
            {
                alreadyStored++;
            }
            else
            {
                await StoreAsync(channel, message, cancellationToken);
                stored++;
            }
        }

        return new WebhookProcessingResult(stored, alreadyStored, ignored);
    }

    // A delivery that names a different business number than the channel's is a misconfigured webhook; its messages are not ours.
    private static bool IsAnotherNumber(ParsedMessage message, WhatsAppChannel channel)
        => channel.ProviderPhoneNumberId is not null
           && message.PhoneNumberId is not null
           && !string.Equals(channel.ProviderPhoneNumberId, message.PhoneNumberId, StringComparison.Ordinal);

    private async Task StoreAsync(WhatsAppChannel channel, ParsedMessage message, CancellationToken cancellationToken)
    {
        var contact = await GetOrCreateContactAsync(channel.TenantId, message, cancellationToken);
        var conversation = await GetOrCreateConversationAsync(channel, contact, message, cancellationToken);

        await _messages.AddAsync(
            Message.Create(channel.TenantId, conversation.Id, channel.Id, message.ProviderMessageId, message.Direction, message.Type, message.Text, message.SentAt),
            cancellationToken);

        if (message.Referral is not null)
        {
            conversation.MarkAsAds(message.Referral.Reference);
        }

        var times = await _messages.ListTimesAsync(conversation.Id, cancellationToken);
        conversation.ApplyTimeline(ConversationTimeline.From(times)!);
        await _conversations.UpdateAsync(conversation, cancellationToken);
    }

    private async Task<Contact> GetOrCreateContactAsync(Guid tenantId, ParsedMessage message, CancellationToken cancellationToken)
    {
        var waId = Contact.NormalizeWaId(message.ContactWaId)!;
        var contact = await _contacts.GetByWaIdAsync(waId, cancellationToken);
        if (contact is null)
        {
            contact = Contact.Create(tenantId, waId, message.ContactName, message.SentAt);
            await _contacts.AddAsync(contact, cancellationToken);
            return contact;
        }

        contact.ObserveAt(message.SentAt);
        contact.UpdateDisplayName(message.ContactName);
        await _contacts.UpdateAsync(contact, cancellationToken);
        return contact;
    }

    // Reuse the contact's open conversation on this channel, otherwise open one (with its opportunity) at the time of the message.
    private async Task<Conversation> GetOrCreateConversationAsync(
        WhatsAppChannel channel, Contact contact, ParsedMessage message, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.FindOpenAsync(contact.Id, channel.Id, cancellationToken);
        if (conversation is not null)
        {
            return conversation;
        }

        conversation = Conversation.Start(
            channel.TenantId, contact.Id, channel.Id, channel.BranchId, message.SentAt,
            message.Referral is null ? ConversationSource.Organic : ConversationSource.Ads,
            message.Referral?.Reference);
        await _conversations.AddAsync(conversation, Opportunity.Open(channel.TenantId, conversation.Id), cancellationToken);
        return conversation;
    }
}
