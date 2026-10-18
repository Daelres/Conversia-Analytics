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
    ParsedReferral? Referral);

/// <summary>The click-to-WhatsApp ad referral of a message. <see cref="Reference"/> is what identifies the ad (headline, URL or id).</summary>
public sealed record ParsedReferral(string? Reference);

/// <param name="Messages">The messages to store, in payload order.</param>
/// <param name="StatusCount">Delivery receipts (<c>statuses</c>), which carry nothing to store.</param>
/// <param name="SkippedCount">
/// Entries that looked like customer or advisor messages but could not be stored: no usable id, no WhatsApp phone number (for example
/// a business-scoped user id), an implausible timestamp, a group message, or an echo whose customer cannot be told.
/// </param>
/// <param name="IgnoredCount">Messages of a kind that says nothing about the conversation (<c>reaction</c>, <c>system</c>).</param>
public sealed record ParsedWebhook(IReadOnlyList<ParsedMessage> Messages, int StatusCount, int SkippedCount, int IgnoredCount)
{
    public static ParsedWebhook Empty { get; } = new([], 0, 0, 0);
}

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
            var counts = new Counts();

            foreach (var value in Values(document.RootElement))
            {
                ParseValue(value, latest, messages, counts);
            }

            return new ParsedWebhook(messages, counts.Statuses, counts.Skipped, counts.Ignored);
        }
    }

    // The "value" objects of the delivery: one per entry/change in the Cloud API envelope, or the root itself in the older shape.
    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    if (change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                    {
                        yield return value;
                    }
                }
            }

            yield break;
        }

        yield return root;
    }

    private sealed class Counts
    {
        public int Statuses;
        public int Skipped;
        public int Ignored;
    }

    private static void ParseValue(JsonElement value, DateTime latest, List<ParsedMessage> messages, Counts counts)
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
                sentByBusiness ? String(raw, "to") ?? onlyContact : from, phoneNumberId, latest, names, messages, counts);
        }

        foreach (var echo in Array(value, "message_echoes"))
        {
            Add(echo, MessageDirection.Outbound, String(echo, "to") ?? onlyContact, phoneNumberId, latest, names, messages, counts);
        }
    }

    private static void Add(
        JsonElement raw, MessageDirection direction, string? contactWaId, string? phoneNumberId, DateTime latest,
        IReadOnlyDictionary<string, string?> names, List<ParsedMessage> messages, Counts counts)
    {
        var type = (String(raw, "type") ?? "unknown").ToLowerInvariant();
        if (type is "reaction" or "system")
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
            direction == MessageDirection.Inbound ? Referral(raw) : null));
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
        var reference = new[] { "headline", "source_url", "source_id", "body" }
            .Select(name => String(referral, name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new ParsedReferral(Clean(reference?.Trim(), Conversation.SourceReferenceMaxLength));
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
