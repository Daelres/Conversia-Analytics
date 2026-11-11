using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Domain.Entities;

namespace Conversia.Application.Sandbox;

/// <summary>What marks the ids the internal sandbox makes up, so nobody mistakes them for a provider's.</summary>
public static class SandboxIds
{
    /// <summary>Every message id of the sandbox starts with this: the simulated customer's messages and the ones the internal sender "sends".</summary>
    public const string MessageIdPrefix = "sandbox.";

    /// <summary>The id of a message the simulated customer writes.</summary>
    public static string NewInboundMessageId() => $"{MessageIdPrefix}in.{Guid.NewGuid():N}";

    /// <summary>The id the internal sender gives a message it accepts instead of a provider.</summary>
    public static string NewOutboundMessageId() => $"{MessageIdPrefix}out.{Guid.NewGuid():N}";
}

/// <summary>
/// Builds the body 360dialog would POST for one text message of a customer, in the WhatsApp Cloud API envelope
/// (<c>entry[].changes[].value.messages</c>) that <c>Dialog360PayloadParser</c> reads (docs/WEBHOOKS.md). It is the same shape the
/// development simulator builds (<c>tools/Conversia.WebhookSimulator/Generation/CloudApiPayloads.cs</c>), reduced to the one message the
/// internal sandbox needs; the simulator keeps its own builder because <c>src</c> must not depend on <c>tools</c>.
/// </summary>
public static class SandboxWebhookPayload
{
    // Readable text in the stored payload (accents, emoji) instead of \u escapes; it is never written into HTML.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string InboundText(WhatsAppChannel channel, string customerWaId, string customerName, string messageId, string text, DateTime sentAtUtc)
    {
        var metadata = new JsonObject { ["display_phone_number"] = Digits(channel.DisplayPhoneNumber) };
        if (channel.ProviderPhoneNumberId is not null)
        {
            // Only when the channel declares one: ingestion skips a delivery that names another number than the channel's.
            metadata["phone_number_id"] = channel.ProviderPhoneNumberId;
        }

        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = metadata,
            ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = customerName }, ["wa_id"] = customerWaId }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["from"] = customerWaId,
                ["id"] = messageId,
                ["timestamp"] = new DateTimeOffset(DateTime.SpecifyKind(sentAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["type"] = "text",
                ["text"] = new JsonObject { ["body"] = text },
            }),
        };

        var envelope = new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "conversia-sandbox",
                ["changes"] = new JsonArray(new JsonObject { ["field"] = "messages", ["value"] = value }),
            }),
        };
        return envelope.ToJsonString(Options);
    }

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());
}
