namespace Conversia.Tests.Shared;

/// <summary>
/// Sample 360dialog / WhatsApp Cloud API webhook deliveries, written after the public documentation (docs.360dialog.com, Meta's
/// "messages" and "smb_message_echoes" webhook references). The numbers are invented (+57 300 555 xxxx). The echo samples are
/// UNVERIFIED against a real 360dialog sandbox. Shared by the parser unit tests and the integration tests.
/// </summary>
public static class Dialog360Samples
{
    public const string PhoneNumberId = "106540352242922";
    public const string BusinessNumber = "573005550100";
    public const string CustomerWaId = "573005550101";

    /// <summary>Unix seconds of 2026-10-18 12:00:00 UTC.</summary>
    public const long T0 = 1_792_324_800;

    private static string Envelope(string field, string value, string metadata = "") => $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            { "id": "102290129340398",
              "changes": [ { "field": "{{field}}", "value": { "messaging_product": "whatsapp", "metadata": { "display_phone_number": "{{BusinessNumber}}", "phone_number_id": "{{PhoneNumberId}}"{{metadata}} }, {{value}} } } ] }
          ]
        }
        """;

    /// <summary>One inbound text message with its contact profile.</summary>
    public static string InboundText(string id = "wamid.HBgM-IN-1", long timestamp = T0, string text = "Hola, quisiera info de depilación láser", string waId = CustomerWaId, string name = "Laura Gómez", string phoneNumberId = PhoneNumberId)
        => $$"""
            {
              "object": "whatsapp_business_account",
              "entry": [ { "id": "102290129340398", "changes": [ { "field": "messages", "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "{{BusinessNumber}}", "phone_number_id": "{{phoneNumberId}}" },
                "contacts": [ { "profile": { "name": "{{name}}" }, "wa_id": "{{waId}}" } ],
                "messages": [ { "from": "{{waId}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "{{text}}" } } ]
              } } ] } ]
            }
            """;

    /// <summary>Two inbound messages of the same customer in one delivery.</summary>
    public static string TwoInboundMessages(long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{CustomerWaId}}" } ],
        "messages": [
          { "from": "{{CustomerWaId}}", "id": "wamid.HBgM-IN-A", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola" } },
          { "from": "{{CustomerWaId}}", "id": "wamid.HBgM-IN-B", "timestamp": "{{timestamp + 5}}", "type": "text", "text": { "body": "¿Cuánto cuesta?" } }
        ]
        """);

    /// <summary>A first message that comes from a click-to-WhatsApp ad (carries a referral).</summary>
    public static string InboundFromAd(string id = "wamid.HBgM-AD-1", long timestamp = T0, string waId = CustomerWaId) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{waId}}" } ],
        "messages": [ { "from": "{{waId}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola, vi su anuncio" },
          "referral": { "source_url": "https://fb.me/ad-123", "source_id": "120200000000000", "source_type": "ad", "headline": "Depilación láser 2x1", "body": "Agenda hoy", "media_type": "image" } } ]
        """);

    /// <summary>An image with a caption and no text body.</summary>
    public static string InboundImage(string id = "wamid.HBgM-IMG-1", long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{CustomerWaId}}" } ],
        "messages": [ { "from": "{{CustomerWaId}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "image", "image": { "id": "9999", "mime_type": "image/jpeg", "sha256": "abc", "caption": "Así quedó" } } ]
        """);

    /// <summary>An audio message: no text at all.</summary>
    public static string InboundAudio(string id = "wamid.HBgM-AUD-1", long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{CustomerWaId}}" } ],
        "messages": [ { "from": "{{CustomerWaId}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "audio", "audio": { "id": "8888", "mime_type": "audio/ogg; codecs=opus", "voice": true } } ]
        """);

    /// <summary>Delivery receipts only: nothing to store.</summary>
    public static string StatusesOnly() => Envelope("messages", $$"""
        "statuses": [
          { "id": "wamid.HBgM-OUT-1", "status": "delivered", "timestamp": "{{T0}}", "recipient_id": "{{CustomerWaId}}" },
          { "id": "wamid.HBgM-OUT-1", "status": "read", "timestamp": "{{T0 + 3}}", "recipient_id": "{{CustomerWaId}}" }
        ]
        """);

    /// <summary>
    /// A message the business sent from the WhatsApp Business app or another device (coexistence echo,
    /// <c>smb_message_echoes</c>): <c>from</c> is the business number, <c>to</c> the customer. UNVERIFIED shape.
    /// </summary>
    public static string OutboundEcho(string id = "wamid.HBgM-ECHO-1", long timestamp = T0 + 60, string to = CustomerWaId, string text = "¡Hola Laura! Con gusto te ayudo.") => Envelope("smb_message_echoes", $$"""
        "message_echoes": [ { "from": "{{BusinessNumber}}", "to": "{{to}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "{{text}}" } } ]
        """);

    /// <summary>The older on-premise shape: contacts/messages at the root, no envelope.</summary>
    public static string LegacyRootShape(string id = "wamid.LEGACY-1", long timestamp = T0) => $$"""
        {
          "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{CustomerWaId}}" } ],
          "messages": [ { "from": "{{CustomerWaId}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola desde el formato antiguo" } } ]
        }
        """;

    /// <summary>A change this system does not model (e.g. a template status update): valid JSON with nothing to store.</summary>
    public static string UnknownEvent() => """
        { "object": "whatsapp_business_account", "entry": [ { "id": "1", "changes": [ { "field": "message_template_status_update", "value": { "event": "APPROVED", "message_template_id": 1 } } ] } ] }
        """;
}
