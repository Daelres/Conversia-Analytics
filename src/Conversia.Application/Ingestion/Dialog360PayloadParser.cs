using System.Globalization;
using System.Text.Json;
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
/// <param name="SkippedCount">Entries that looked like messages but lacked a usable id, contact or timestamp.</param>
public sealed record ParsedWebhook(IReadOnlyList<ParsedMessage> Messages, int StatusCount, int SkippedCount)
{
    public static ParsedWebhook Empty { get; } = new([], 0, 0);
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

    public static ParsedWebhook Parse(string json)
    {
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
            var statuses = 0;
            var skipped = 0;

            foreach (var value in Values(document.RootElement))
            {
                ParseValue(value, messages, ref statuses, ref skipped);
            }

            return new ParsedWebhook(messages, statuses, skipped);
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

    private static void ParseValue(JsonElement value, List<ParsedMessage> messages, ref int statuses, ref int skipped)
    {
        var phoneNumberId = String(Object(value, "metadata"), "phone_number_id");
        var businessDigits = Contact.NormalizeWaId(String(Object(value, "metadata"), "display_phone_number"));
        var names = ContactNames(value);
        var firstContact = names.Count > 0 ? names.Keys.First() : null;

        statuses += Array(value, "statuses").Count();

        foreach (var raw in Array(value, "messages"))
        {
            var from = String(raw, "from");
            // Fallback for an echo delivered as a plain message: the sender is the business number, the customer is the recipient.
            var sentByBusiness = businessDigits is not null && Contact.NormalizeWaId(from) == businessDigits;
            Add(raw, sentByBusiness ? MessageDirection.Outbound : MessageDirection.Inbound,
                sentByBusiness ? String(raw, "to") ?? firstContact : from, phoneNumberId, names, messages, ref skipped);
        }

        foreach (var echo in Array(value, "message_echoes"))
        {
            Add(echo, MessageDirection.Outbound, String(echo, "to") ?? firstContact, phoneNumberId, names, messages, ref skipped);
        }
    }

    private static void Add(
        JsonElement raw, MessageDirection direction, string? contactWaId, string? phoneNumberId,
        IReadOnlyDictionary<string, string?> names, List<ParsedMessage> messages, ref int skipped)
    {
        var id = String(raw, "id")?.Trim();
        var sentAt = Timestamp(raw);
        var normalizedContact = Contact.NormalizeWaId(contactWaId);
        if (string.IsNullOrEmpty(id) || id.Length > Message.ProviderMessageIdMaxLength || id.Contains('\0') || sentAt is null || normalizedContact is null)
        {
            skipped++;
            return;
        }

        var type = (String(raw, "type") ?? "unknown").ToLowerInvariant();
        if (!Message.IsValidType(type))
        {
            type = "unknown";
        }

        names.TryGetValue(normalizedContact, out var name);
        messages.Add(new ParsedMessage(
            phoneNumberId,
            contactWaId!,
            direction == MessageDirection.Inbound ? name : null,
            id,
            direction,
            type,
            Clean(Text(raw, type), Message.TextMaxLength),
            sentAt.Value,
            direction == MessageDirection.Inbound ? Referral(raw) : null));
    }

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

        var reference = String(referral, "headline") ?? String(referral, "source_url") ?? String(referral, "source_id") ?? String(referral, "body");
        return new ParsedReferral(Clean(reference, Conversation.SourceReferenceMaxLength));
    }

    // Unix seconds, as a string ("1739400000") or a number.
    private static DateTime? Timestamp(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var raw))
        {
            return null;
        }

        long seconds;
        switch (raw.ValueKind)
        {
            case JsonValueKind.String when long.TryParse(raw.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds):
                break;
            case JsonValueKind.Number when raw.TryGetInt64(out seconds):
                break;
            default:
                return null;
        }

        // 1970-01-02 .. year 9999: anything else is not a WhatsApp timestamp.
        return seconds is > 86_400 and < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    }

    private static string? Clean(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = text.Replace("\0", string.Empty);
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
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
