using System.Globalization;
using System.Text.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.Ingestion;

/// <summary>A message found in a webhook delivery, in the provider-neutral shape ingestion works with.</summary>
/// <param name="PhoneNumberId">The business number the delivery says it belongs to (<c>metadata.phone_number_id</c>), when present.</param>
/// <param name="ContactWaId">The customer's WhatsApp id as the payload wrote it (not normalized yet).</param>
public sealed record ParsedMessage(
    string? PhoneNumberId,
    string ContactWaId,
    string? ContactName,
    string ProviderMessageId,
    MessageDirection Direction,
    string Type,
    string? Text,
    DateTime SentAt,
    ParsedReferral? Referral,
    MessageMedia? Media = null,
    bool Imported = false);

/// <summary>
/// The click-to-WhatsApp ad referral of a message. <see cref="Reference"/> is what identifies the ad (headline, URL or id); <see cref="Ad"/>
/// its structured data (source type, id, URL, headline), never the customer's (docs/WEBHOOKS.md, "Ad referral").
/// </summary>
public sealed record ParsedReferral(string? Reference, AdReferral? Ad = null);

/// <param name="Messages">The messages to store, in payload order.</param>
/// <param name="StatusCount">Delivery receipts (<c>statuses</c>), which carry nothing to store.</param>
/// <param name="SkippedCount">
/// Entries that looked like customer or advisor messages but could not be stored: no usable id, no WhatsApp phone number (for example
/// a business-scoped user id), an implausible timestamp, a group message, or an echo whose customer cannot be told.
/// </param>
/// <param name="IgnoredCount">Messages of a kind that says nothing about the conversation (<c>reaction</c>, <c>system</c>).</param>
/// <param name="TemplateStatuses">Template status notifications (<c>message_template_status_update</c>, docs/WEBHOOKS.md).</param>
public sealed record ParsedWebhook(
    IReadOnlyList<ParsedMessage> Messages, int StatusCount, int SkippedCount, int IgnoredCount, IReadOnlyList<ParsedTemplateStatus>? TemplateStatuses = null)
{
    public static ParsedWebhook Empty { get; } = new([], 0, 0, 0);

    public IReadOnlyList<ParsedTemplateStatus> TemplateStatuses { get; init; } = TemplateStatuses ?? [];

    /// <summary>The chunks of a shared chat history (<c>history</c> field), with their phase and progress; their messages are in <see cref="Messages"/>, marked imported.</summary>
    public IReadOnlyList<ParsedHistoryChunk> HistoryChunks { get; init; } = [];

    /// <summary>Address-book entries of the WhatsApp Business app (<c>smb_app_state_sync</c>): counted, never kept.</summary>
    public int AppStateSyncIgnored { get; init; }

    /// <summary>Coexistence notifications of the number (<c>account_update</c>): partner removed, offboarded, reconnected.</summary>
    public IReadOnlyList<ParsedAccountUpdate> AccountUpdates { get; init; } = [];
}

/// <summary>One chunk of a shared chat history: phase 0 (day 0-1), 1 (day 1-90) or 2 (day 90-180), its order and the overall progress (0-100).</summary>
/// <param name="Declined">The business turned history sharing off (Meta's error 2593109): no messages come.</param>
public sealed record ParsedHistoryChunk(int? Phase, int? ChunkOrder, int? Progress, int MessageCount, bool Declined);

/// <summary>A coexistence <c>account_update</c>: the event as Meta names it and, for <c>PARTNER_REMOVED</c>, the disconnection reason.</summary>
public sealed record ParsedAccountUpdate(string Event, string? Reason, DateTime? OccurredAt);

/// <summary>
/// A template status notification, in Meta's words (<c>event</c> as given: <c>APPROVED</c>, <c>PAUSED</c>, ...). The template is named by
/// name and language, which is how the channel's copy is keyed.
/// </summary>
/// <param name="OccurredAt">The <c>entry.time</c> of the notification; null when absent (the time of processing is used then).</param>
public sealed record ParsedTemplateStatus(string Name, string Language, string Event, string? Category, DateTime? OccurredAt = null);

/// <summary>
/// The only place that knows the shape of a 360dialog webhook delivery. It reads the WhatsApp Cloud API format
/// (<c>entry[].changes[].value</c>) and, defensively, the older on-premise shape where <c>contacts</c>/<c>messages</c>/<c>statuses</c> sit at the
/// root. It never throws on content: whatever it cannot use is skipped and counted, so an unknown event is stored and later
/// processed as "nothing to do" instead of failing.
/// <para>
/// UNVERIFIED against a real 360dialog sandbox: the outbound echoes of messages sent from the WhatsApp Business app or another
/// device (coexistence). They are read from <c>message_echoes[]</c> (the <c>smb_message_echoes</c> field of Meta's documentation)
/// and, as a fallback, from a <c>messages[]</c> entry whose sender is the business number itself. See docs/WEBHOOKS.md.
/// </para>
/// </summary>
public static class Dialog360PayloadParser
{
    private const int MaxContactNameLength = Contact.DisplayNameMaxLength;

    // WhatsApp Business started in 2009 and the Cloud API in 2022; nothing before 2015 is a plausible timestamp. A day of clock skew is tolerated.
    private static readonly DateTime EarliestTimestamp = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan FutureSkew = TimeSpan.FromDays(1);

    /// <param name="nowUtc">The current time, to reject timestamps from the future; the system clock when null.</param>
    public static ParsedWebhook Parse(string json, DateTime? nowUtc = null)
    {
        var latest = (nowUtc ?? DateTime.UtcNow) + FutureSkew;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return ParsedWebhook.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ParsedWebhook.Empty;
            }

            var messages = new List<ParsedMessage>();
            var templates = new List<ParsedTemplateStatus>();
            var chunks = new List<ParsedHistoryChunk>();
            var accountUpdates = new List<ParsedAccountUpdate>();
            var counts = new Counts();

            foreach (var (field, value, time) in Values(document.RootElement))
            {
                switch (field)
                {
                    case TemplateStatusField:
                        ParseTemplateStatus(value, time, templates, counts);
                        break;
                    case HistoryField:
                        // The shared chat history of a coexistence number: its messages are marked imported, so ingestion stores them only
                        // when the channel opted in (docs/WEBHOOKS.md, "History import"). A media follow-up comes as messages[] here too.
                        ParseHistory(value, latest, messages, chunks, counts);
                        ParseValue(value, latest, messages, counts, imported: true);
                        break;
                    case AppStateSyncField:
                        // The phone's address book: never stored (minimisation, docs/decisiones.md); only counted.
                        counts.AppStateSync += Array(value, "state_sync").Count()
                                               + (value.TryGetProperty("count", out var redacted) && redacted.ValueKind == JsonValueKind.Number && redacted.TryGetInt32(out var n) && n > 0 ? n : 0);
                        break;
                    case AccountUpdateField:
                        ParseAccountUpdate(value, time, accountUpdates);
                        break;
                    default:
                        ParseValue(value, latest, messages, counts, imported: false);
                        break;
                }
            }

            return new ParsedWebhook(messages, counts.Statuses, counts.Skipped, counts.Ignored, templates)
            {
                HistoryChunks = chunks,
                AppStateSyncIgnored = counts.AppStateSync,
                AccountUpdates = accountUpdates,
            };
        }
    }

    /// <summary>The <c>field</c> of a template status notification in Meta's webhook envelope.</summary>
    public const string TemplateStatusField = "message_template_status_update";

    /// <summary>The <c>field</c> of a shared chat history chunk (coexistence onboarding).</summary>
    public const string HistoryField = "history";

    /// <summary>The <c>field</c> of the WhatsApp Business app's contact sync (coexistence). Never stored.</summary>
    public const string AppStateSyncField = "smb_app_state_sync";

    /// <summary>The <c>field</c> of account notifications, among them the coexistence disconnections.</summary>
    public const string AccountUpdateField = "account_update";

    /// <summary>The <c>account_update</c> events that say something about a coexistence number (Meta's reference).</summary>
    public static readonly IReadOnlySet<string> CoexistenceEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "PARTNER_REMOVED", "ACCOUNT_OFFBOARDED", "ACCOUNT_RECONNECTED",
    };

    // Echo types that change an earlier message rather than being one (Meta's smb_message_echoes reference): not stored.
    private static readonly HashSet<string> IgnoredTypes = ["reaction", "system", "revoke", "edit"];

    private static void ParseAccountUpdate(JsonElement value, DateTime? occurredAt, List<ParsedAccountUpdate> updates)
    {
        var accountEvent = String(value, "event");
        if (accountEvent is null || !CoexistenceEvents.Contains(accountEvent))
        {
            return;
        }

        // An upper-case word only (PRIMARY_INACTIVITY, ...): anything else is dropped, the event stays.
        var reason = String(Object(value, "disconnection_info"), "reason");
        if (reason is not { Length: >= 1 and <= 50 } || !reason.All(c => char.IsAsciiLetterUpper(c) || c == '_'))
        {
            reason = null;
        }

        updates.Add(new ParsedAccountUpdate(accountEvent, reason, occurredAt));
    }

    // history[]: chunks with their metadata and threads; or an errors[] entry when the business declined sharing (code 2593109). Each
    // thread is one customer (id or context.wa_id); a message from the business number is outbound, any other inbound. There is no "to".
    private static void ParseHistory(JsonElement value, DateTime latest, List<ParsedMessage> messages, List<ParsedHistoryChunk> chunks, Counts counts)
    {
        var phoneNumberId = String(Object(value, "metadata"), "phone_number_id");
        var businessDigits = Digits(String(Object(value, "metadata"), "display_phone_number"));
        foreach (var chunk in Array(value, "history"))
        {
            var metadata = Object(chunk, "metadata");
            var declined = Array(chunk, "errors").Any();
            var before = messages.Count;
            foreach (var thread in Array(chunk, "threads"))
            {
                var customer = String(Object(thread, "context"), "wa_id") ?? String(thread, "id");
                foreach (var raw in Array(thread, "messages"))
                {
                    var from = String(raw, "from");
                    var byBusiness = businessDigits.Length >= Contact.MinWaIdLength && Digits(from) == businessDigits;
                    Add(raw, byBusiness ? MessageDirection.Outbound : MessageDirection.Inbound, customer, phoneNumberId, latest,
                        new Dictionary<string, string?>(), messages, counts, imported: true);
                }
            }

            chunks.Add(new ParsedHistoryChunk(Int(metadata, "phase"), Int(metadata, "chunk_order"), Int(metadata, "progress"), messages.Count - before, declined));
        }
    }

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number
           && child.TryGetInt32(out var value)
            ? value
            : null;

    private static void ParseTemplateStatus(JsonElement value, DateTime? occurredAt, List<ParsedTemplateStatus> templates, Counts counts)
    {
        var name = String(value, "message_template_name");
        var language = String(value, "message_template_language");
        var statusEvent = String(value, "event");
        if (!MessageTemplate.IsValidName(name) || !MessageTemplate.IsValidLanguage(language) || string.IsNullOrWhiteSpace(statusEvent) || statusEvent.Length > 50)
        {
            counts.Skipped++;
            return;
        }

        var category = String(value, "message_template_category");
        templates.Add(new ParsedTemplateStatus(name!, language!, statusEvent, category is { Length: <= 50 } ? category : null, occurredAt));
    }

    // The "value" objects of the delivery with the field of their change: one per entry/change in the Cloud API envelope, or the root
    // itself (no field) in the older shape.
    private static IEnumerable<(string? Field, JsonElement Value, DateTime? Time)> Values(JsonElement root)
    {
        if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                DateTime? time = entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var seconds)
                                 && seconds is > 0 and < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    : null;
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    if (change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                    {
                        yield return (String(change, "field"), value, time);
                    }
                }
            }

            yield break;
        }

        yield return (null, root, null);
    }

    private sealed class Counts
    {
        public int Statuses;
        public int Skipped;
        public int Ignored;
        public int AppStateSync;
    }

    private static void ParseValue(JsonElement value, DateTime latest, List<ParsedMessage> messages, Counts counts, bool imported)
    {
        var metadata = Object(value, "metadata");
        var phoneNumberId = String(metadata, "phone_number_id");
        var businessDigits = Digits(String(metadata, "display_phone_number"));
        var names = ContactNames(value);
        // The customer of a message that does not name one: only when the delivery has exactly one contact, never a guess among several.
        var contactEntries = Array(value, "contacts").ToList();
        var onlyContact = contactEntries.Count == 1 ? String(contactEntries[0], "wa_id") : null;

        counts.Statuses += Array(value, "statuses").Count();

        foreach (var raw in Array(value, "messages"))
        {
            var from = String(raw, "from");
            // Fallback for an echo delivered as a plain message: the sender is the business number, the customer is the recipient.
            var sentByBusiness = businessDigits.Length >= Contact.MinWaIdLength && Digits(from) == businessDigits;
            Add(raw, sentByBusiness ? MessageDirection.Outbound : MessageDirection.Inbound,
                sentByBusiness ? String(raw, "to") ?? onlyContact : from, phoneNumberId, latest, names, messages, counts, imported);
        }

        foreach (var echo in Array(value, "message_echoes"))
        {
            Add(echo, MessageDirection.Outbound, String(echo, "to") ?? onlyContact, phoneNumberId, latest, names, messages, counts, imported);
        }
    }

    private static void Add(
        JsonElement raw, MessageDirection direction, string? contactWaId, string? phoneNumberId, DateTime latest,
        IReadOnlyDictionary<string, string?> names, List<ParsedMessage> messages, Counts counts, bool imported)
    {
        var type = (String(raw, "type") ?? "unknown").ToLowerInvariant();
        if (IgnoredTypes.Contains(type))
        {
            counts.Ignored++;
            return;
        }

        var id = String(raw, "id")?.Trim();
        var sentAt = Timestamp(raw, latest);
        var normalizedContact = Contact.NormalizeWaId(contactWaId);
        if (IsGroupMessage(raw)
            || string.IsNullOrEmpty(id) || id.Length > Message.ProviderMessageIdMaxLength || id.Contains('\0')
            || sentAt is null || normalizedContact is null)
        {
            counts.Skipped++;
            return;
        }

        if (!Message.IsValidType(type))
        {
            type = "unknown";
        }

        names.TryGetValue(normalizedContact, out var name);
        messages.Add(new ParsedMessage(
            phoneNumberId,
            normalizedContact,
            direction == MessageDirection.Inbound ? name : null,
            id,
            direction,
            type,
            Clean(Text(raw, type), Message.TextMaxLength),
            sentAt.Value,
            direction == MessageDirection.Inbound && !imported ? Referral(raw) : null,
            Media(raw, type),
            imported));
    }

    // The media types of the Cloud API, each with its object under the type's name.
    private static readonly HashSet<string> MediaTypes = ["image", "audio", "video", "document", "sticker"];

    // The metadata of a media message, never the file: the provider's id, the MIME type (lower case, parameters such as "; codecs=opus"
    // dropped, an odd value dropped) and whether it has a caption. The document's file name is not read (it can carry personal data).
    private static MessageMedia? Media(JsonElement message, string type)
    {
        if (!MediaTypes.Contains(type))
        {
            return null;
        }

        var media = Object(message, type);
        var id = String(media, "id")?.Replace("\0", string.Empty).Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MessageMedia.MaxIdLength)
        {
            id = null;
        }

        var mime = String(media, "mime_type")?.Split(';')[0].Trim().ToLowerInvariant();
        if (!MessageMedia.IsValidMimeType(mime))
        {
            mime = null;
        }

        return new MessageMedia(id, mime, !string.IsNullOrWhiteSpace(String(media, "caption")));
    }

    // Group messages (Groups API) carry a group id, or say their recipient is a group. They are not a customer conversation.
    private static bool IsGroupMessage(JsonElement raw)
        => (raw.TryGetProperty("group_id", out var group) && group.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
           || string.Equals(String(raw, "recipient_type"), "group", StringComparison.OrdinalIgnoreCase);

    // Only the digits of a formatted phone number ("+57 300 555 0100"), to compare the business number with a sender.
    private static string Digits(string? text) => text is null ? string.Empty : new string(text.Where(char.IsAsciiDigit).ToArray());

    // wa_id -> profile name, keyed by the normalized id so a message's sender finds its name whatever formatting the payload used.
    private static Dictionary<string, string?> ContactNames(JsonElement value)
    {
        var names = new Dictionary<string, string?>();
        foreach (var contact in Array(value, "contacts"))
        {
            var waId = Contact.NormalizeWaId(String(contact, "wa_id"));
            if (waId is not null)
            {
                names[waId] = Clean(String(Object(contact, "profile"), "name"), MaxContactNameLength);
            }
        }

        return names;
    }

    private static string? Text(JsonElement message, string type)
    {
        var text = String(Object(message, "text"), "body")
                   ?? String(Object(message, "button"), "text")
                   ?? String(Object(Object(message, "interactive"), "button_reply"), "title")
                   ?? String(Object(Object(message, "interactive"), "list_reply"), "title");

        // The caption of a media message is the only text it has.
        return text ?? (type is "image" or "video" or "document" or "audio" ? String(Object(message, type), "caption") : null);
    }

    private static ParsedReferral? Referral(JsonElement message)
    {
        var referral = Object(message, "referral");
        if (referral.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The first of these that has something in it: an empty headline must not hide the URL.
        // A URL keeps only its scheme, host and path (tracking parameters can identify the click); a value that is not an http(s) URL is skipped.
        var reference = new[] { "headline", "source_url", "source_id", "body" }
            .Select(name => name == "source_url" ? AdReferral.SafeUrl(String(referral, name)) : String(referral, name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        // The ad's data only: the click id (ctwa_clid), the ad's body, media URLs and welcome message are not kept (docs/decisiones.md).
        var ad = AdReferral.Create(String(referral, "source_type"), String(referral, "source_id"), String(referral, "source_url"), String(referral, "headline"));
        return new ParsedReferral(Clean(reference?.Trim(), Conversation.SourceReferenceMaxLength), ad);
    }

    // Unix seconds, as a string or a number, possibly with decimals or an exponent ("1792324800.0", "1.7923248e9"); truncated to the second.
    // Only a plausible time is accepted: between 2015 and a day from now.
    private static DateTime? Timestamp(JsonElement message, DateTime latest)
    {
        if (!message.TryGetProperty("timestamp", out var raw))
        {
            return null;
        }

        double seconds;
        switch (raw.ValueKind)
        {
            case JsonValueKind.String when double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds):
                break;
            case JsonValueKind.Number when raw.TryGetDouble(out seconds):
                break;
            default:
                return null;
        }

        var earliest = new DateTimeOffset(EarliestTimestamp).ToUnixTimeSeconds();
        var last = new DateTimeOffset(latest).ToUnixTimeSeconds();
        if (double.IsNaN(seconds) || seconds < earliest || seconds > last)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds)).UtcDateTime;
    }

    private static string? Clean(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        return TextRules.Truncate(text.Replace("\0", string.Empty), maxLength);
    }

    private static JsonElement Object(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : default;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array
            ? child.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;
}
