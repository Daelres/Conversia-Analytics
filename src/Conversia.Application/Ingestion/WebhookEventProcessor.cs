using Conversia.Application.Assistant;
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
    private readonly IContactSuppressionRepository? _suppressions;
    private readonly IMessageTemplateRepository? _templates;
    private readonly IAssistantSignals? _assistant;

    // The conversations of this delivery that got a live (not imported) customer message, and those that got an echo of the phone app.
    private readonly Dictionary<Guid, List<string?>> _customerWrote = [];
    private readonly Dictionary<Guid, List<string?>> _personWrote = [];

    /// <param name="suppressions">The marks of suppressed contacts: their old messages are dropped (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression").</param>
    public WebhookEventProcessor(
        IWhatsAppChannelRepository channels, IContactRepository contacts, IConversationRepository conversations,
        IMessageRepository messages, TimeProvider timeProvider, WebhookIngestionLimits limits, ILogger<WebhookEventProcessor> logger,
        IContactSuppressionRepository? suppressions = null, IMessageTemplateRepository? templates = null, IAssistantSignals? assistant = null)
    {
        _assistant = assistant;
        _templates = templates;
        _suppressions = suppressions;
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
        _customerWrote.Clear();
        _personWrote.Clear();

        // A channel that cannot be found here would be a deleted or foreign one: fail (and retry), never guess.
        var channel = await _channels.GetByIdAsync(webhookEvent.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the webhook event no longer exists.");

        var parsed = Dialog360PayloadParser.Parse(webhookEvent.RawPayload, _timeProvider.GetUtcNow().UtcDateTime);

        // The shared chat history (coexistence onboarding) is stored only when the channel opted in (docs/WEBHOOKS.md, "History import"):
        // otherwise its messages are dropped here, counted, and the delivery is processed as "nothing to do".
        var historyIgnored = 0;
        var messagesToStore = parsed.Messages;
        if (!channel.HistoryImportEnabled && parsed.Messages.Any(m => m.Imported))
        {
            historyIgnored = parsed.Messages.Count(m => m.Imported);
            messagesToStore = parsed.Messages.Where(m => !m.Imported).ToList();
        }

        // Bounded work per delivery: a bigger one would hold the tenant's queue (and a transaction) for too long, and no retry changes its size.
        // A history chunk has its own, larger bound.
        var limit = parsed.HistoryChunks.Count > 0 ? _limits.MaxHistoryMessagesPerDelivery : _limits.MaxMessagesPerDelivery;
        if (messagesToStore.Count + parsed.SkippedCount > limit)
        {
            throw new WebhookPermanentFailureException(
                $"The delivery holds {messagesToStore.Count + parsed.SkippedCount} messages, more than the {limit} allowed per delivery.");
        }

        var crossedTheWall = new HashSet<Guid>(); // contacts whose messages were refused because their mark is not the delivery's
        var suppressedUntil = new Dictionary<string, DateTime?>(StringComparer.Ordinal); // per number of the delivery, looked up once
        var suppressed = 0;
        var stored = 0;
        var alreadyStored = 0;
        var skipped = parsed.SkippedCount;
        var contacts = new Dictionary<string, Contact>();
        var conversations = new Dictionary<(Guid Contact, bool Imported), Conversation>(); // one open (or imported) conversation per contact on this channel

        // Oldest first, so the message that opens a conversation is the first one in time. Arrival order never decides anything
        // that matters: the conversation's markers are recomputed from the stored message times once all its messages are in.
        foreach (var message in messagesToStore.OrderBy(m => m.SentAt).ThenBy(m => m.ProviderMessageId, StringComparer.Ordinal))
        {
            if (IsAnotherNumber(message, channel))
            {
                skipped++;
            }
            else if (await IsSuppressedAsync(channel.TenantId, message, suppressedUntil, cancellationToken))
            {
                // A message of a suppressed contact, sent before its suppression (a delivery still queued, or re-sent by the provider):
                // storing it would bring the contact back. A message sent after it opens a new contact.
                skipped++;
                suppressed++;
            }
            else if (await _messages.FindAsync(channel.Id, message.ProviderMessageId, cancellationToken) is { } existing)
            {
                alreadyStored++;
                await CompletePlaceholderAsync(existing, message, cancellationToken);
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
            // Imported history is read by the rules; by the AI only when the company opted in (each conversation costs, docs/decisiones.md).
            conversation.RequestClassification(now, includeLlm: !conversation.IsImported || channel.HistoryAiAnalysisEnabled);
            await SignalAssistantAsync(conversation, channel, now, cancellationToken);
            await _conversations.UpdateAsync(conversation, cancellationToken);
        }

        var accountUpdates = 0;
        if (parsed.AccountUpdates.Count > 0 && await _channels.GetForUpdateAsync(channel.Id, cancellationToken) is { } tracked)
        {
            foreach (var update in parsed.AccountUpdates)
            {
                // Coexistence notifications (docs/WEBHOOKS.md, "Coexistence"): the delivery came on this channel's URL, so they are this number's.
                if (tracked.ApplyAccountUpdate(update.Event, update.Reason, update.OccurredAt ?? now))
                {
                    accountUpdates++;
                    _logger.LogWarning(
                        "Channel {ChannelId} of tenant {TenantId} received the coexistence notification {Event} ({Reason}).",
                        tracked.Id, tracked.TenantId, update.Event, tracked.ProviderDisconnectionReason ?? "no reason");
                }
            }

            if (accountUpdates > 0)
            {
                await _channels.UpdateAsync(tracked, cancellationToken);
            }
        }

        if (historyIgnored > 0 || parsed.HistoryChunks.Any(c => c.Declined) || parsed.AppStateSyncIgnored > 0)
        {
            // Counts only: no number, no name, no text.
            _logger.LogInformation(
                "Webhook event {EventId} of channel {ChannelId}: {HistoryStored} history messages handled, {HistoryIgnored} ignored (import off), history declined: {Declined}, {AddressBook} address-book entries not stored.",
                webhookEvent.Id, channel.Id, messagesToStore.Count(m => m.Imported), historyIgnored, parsed.HistoryChunks.Any(c => c.Declined), parsed.AppStateSyncIgnored);
        }

        if (crossedTheWall.Count > 0)
        {
            // It should never happen (the simulated customer's numbers are made up by the API, and no real customer can have one): somebody
            // must look. Ids only: no number, no name, no text.
            _logger.LogWarning(
                "Webhook event {EventId} of tenant {TenantId} on channel {ChannelId} (sandbox delivery: {IsSandbox}) carried messages for contacts of the other kind, which were not stored: {ContactIds}.",
                webhookEvent.Id, webhookEvent.TenantId, webhookEvent.ChannelId, webhookEvent.IsSandbox, string.Join(", ", crossedTheWall));
        }

        if (suppressed > 0)
        {
            _logger.LogInformation(
                "Webhook event {EventId} of tenant {TenantId} carried {Count} messages of suppressed contacts sent before their suppression; they were not stored.",
                webhookEvent.Id, webhookEvent.TenantId, suppressed);
        }

        var templateStatuses = await ApplyTemplateStatusesAsync(webhookEvent, channel, parsed.TemplateStatuses, cancellationToken);

        return new WebhookProcessingResult(
            stored, alreadyStored, skipped, parsed.IgnoredCount + parsed.AppStateSyncIgnored, parsed.StatusCount, suppressed, templateStatuses, historyIgnored, accountUpdates);
    }

    // A history media_placeholder is followed by a delivery that carries the media under the same message id (Meta's history reference).
    private async Task CompletePlaceholderAsync(Message existing, ParsedMessage message, CancellationToken cancellationToken)
    {
        if (message.Imported && existing.IsImported && existing.Type == Message.HistoryPlaceholderType && message.Media is { } media
            && message.Type != Message.HistoryPlaceholderType)
        {
            existing.CompleteHistoryPlaceholder(message.Type, media, _timeProvider.GetUtcNow().UtcDateTime);
            await _messages.UpdateAsync(existing, cancellationToken);
        }
    }

    // A template's status changed at Meta (docs/WEBHOOKS.md, "Template status"): the channel's copy follows, so a paused or disabled template
    // stops being offered before the next sync. Only the status: the next sync brings the rest. A template the channel has not synced yet
    // is left for that sync.
    private async Task<int> ApplyTemplateStatusesAsync(
        WebhookEvent webhookEvent, WhatsAppChannel channel, IReadOnlyList<ParsedTemplateStatus> updates, CancellationToken cancellationToken)
    {
        if (_templates is null || updates.Count == 0)
        {
            return 0;
        }

        var applied = 0;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var update in updates)
        {
            if (Messaging.TemplateNames.StatusOfEvent(update.Event) is { } status
                && await _templates.ChangeStatusAsync(channel.Id, update.Name, update.Language, status, update.OccurredAt ?? now, cancellationToken))
            {
                applied++;
            }
        }

        _logger.LogInformation(
            "Webhook event {EventId} of channel {ChannelId} carried {Count} template status notifications; {Applied} changed a stored template.",
            webhookEvent.Id, channel.Id, updates.Count, applied);
        return applied;
    }

    private async Task<bool> IsSuppressedAsync(Guid tenantId, ParsedMessage message, Dictionary<string, DateTime?> cache, CancellationToken cancellationToken)
    {
        if (_suppressions is null)
        {
            return false;
        }

        if (!cache.TryGetValue(message.ContactWaId, out var until))
        {
            until = await _suppressions.GetSuppressedUntilAsync(tenantId, message.ContactWaId, cancellationToken);
            cache[message.ContactWaId] = until;
        }

        return until is { } cutoff && message.SentAt <= cutoff;
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
        Dictionary<(Guid Contact, bool Imported), Conversation> conversations, CancellationToken cancellationToken)
    {
        var contact = await GetOrCreateContactAsync(channel.TenantId, message, isSandbox, contacts, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var conversation = await GetOrCreateConversationAsync(channel, contact, message, conversations, cancellationToken);

        // Who sent it (docs/MESSAGING.md, "Who sent it"): an inbound message is the customer's; an outbound one that arrives through the
        // webhook is an echo of the WhatsApp Business app. The app does not say who typed it, so no author is recorded; the advisor assigned
        // to the conversation right now is kept as context (AssignedAdvisorIdAtReceipt, docs/decisiones.md). A message sent from Conversia
        // that comes back as an echo is already stored and is not added again; one whose echo arrives first is claimed by the send path.
        var echo = message.Direction == MessageDirection.Outbound;
        await _messages.AddAsync(
            Message.Create(
                channel.TenantId, conversation.Id, channel.Id, message.ProviderMessageId, message.Direction, message.Type, message.Text, message.SentAt,
                echo ? MessageSender.PhoneApp : MessageSender.Customer, media: message.Media,
                assignedAdvisorIdAtReceipt: echo && !message.Imported ? conversation.AdvisorId : null, imported: message.Imported),
            cancellationToken);

        if (message.Referral is not null)
        {
            conversation.MarkAsAds(message.Referral.Reference, message.Referral.Ad);
        }

        if (!message.Imported)
        {
            var byConversation = echo ? _personWrote : _customerWrote;
            if (!byConversation.TryGetValue(conversation.Id, out var texts))
            {
                byConversation[conversation.Id] = texts = [];
            }

            texts.Add(message.Text);
        }

        return true;
    }

    // The AI sales assistant (docs/ASSISTANT.md): an echo of the phone app is a person answering (it cancels the timer and holds the
    // assistant back); then, if the customer still wrote last, their message arms the timer. Same transaction, same lock as the messages.
    private async Task SignalAssistantAsync(Conversation conversation, WhatsAppChannel channel, DateTime now, CancellationToken cancellationToken)
    {
        if (_assistant is null)
        {
            return;
        }

        if (_personWrote.TryGetValue(conversation.Id, out var echoes))
        {
            await _assistant.HumanWroteAsync(conversation, now, echoes, cancellationToken);
        }

        if (_customerWrote.TryGetValue(conversation.Id, out var texts) && conversation.AwaitingReply)
        {
            await _assistant.CustomerWroteAsync(conversation, channel, now, texts, cancellationToken);
        }
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
        WhatsAppChannel channel, Contact contact, ParsedMessage message, Dictionary<(Guid Contact, bool Imported), Conversation> conversations,
        CancellationToken cancellationToken)
    {
        if (conversations.TryGetValue((contact.Id, message.Imported), out var known))
        {
            return known;
        }

        // A history message never joins a live conversation, and a live one never joins the imported (closed) conversation of the contact.
        var conversation = message.Imported
            ? await _conversations.FindImportedAsync(contact.Id, channel.Id, cancellationToken)
            : await _conversations.FindOpenAsync(contact.Id, channel.Id, cancellationToken);
        if (conversation is null)
        {
            conversation = Conversation.Start(
                channel.TenantId, contact.Id, channel.Id, channel.BranchId, message.SentAt,
                message.Referral is null ? ConversationSource.Organic : ConversationSource.Ads,
                message.Referral?.Reference,
                contact.IsSandbox,
                message.Referral?.Ad,
                isImported: message.Imported);
            await _conversations.AddAsync(conversation, Opportunity.Open(channel.TenantId, conversation.Id), cancellationToken);
        }

        conversations[(contact.Id, message.Imported)] = conversation;
        return conversation;
    }
}
