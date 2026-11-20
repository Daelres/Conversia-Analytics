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

    /// <summary>An inbound message whose sender is not a phone number (for example a business-scoped user id, <c>CO.1349...</c>).</summary>
    public static string InboundFrom(string from, string id = "wamid.HBgM-BSUID-1", long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{from}}", "user_id": "{{from}}" } ],
        "messages": [ { "from": "{{from}}", "from_user_id": "{{from}}", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola" } } ]
        """);

    /// <summary>Two inbound messages of the same customer in one delivery.</summary>
    public static string TwoInboundMessages(long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Laura Gómez" }, "wa_id": "{{CustomerWaId}}" } ],
        "messages": [
          { "from": "{{CustomerWaId}}", "id": "wamid.HBgM-IN-A", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola" } },
          { "from": "{{CustomerWaId}}", "id": "wamid.HBgM-IN-B", "timestamp": "{{timestamp + 5}}", "type": "text", "text": { "body": "¿Cuánto cuesta?" } }
        ]
        """);

    /// <summary>One delivery with a message of each of two customers.</summary>
    public static string TwoContacts(string firstWaId, string secondWaId, string firstId, string secondId, long timestamp = T0) => Envelope("messages", $$"""
        "contacts": [ { "profile": { "name": "Cliente A" }, "wa_id": "{{firstWaId}}" }, { "profile": { "name": "Cliente B" }, "wa_id": "{{secondWaId}}" } ],
        "messages": [
          { "from": "{{firstWaId}}", "id": "{{firstId}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Hola" } },
          { "from": "{{secondWaId}}", "id": "{{secondId}}", "timestamp": "{{timestamp + 5}}", "type": "text", "text": { "body": "Buenas" } }
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
    /// <summary>
    /// A template status notification, after Meta's <c>message_template_status_update</c> webhook reference (the fields <c>event</c>,
    /// <c>message_template_id</c>, <c>message_template_name</c>, <c>message_template_language</c>, <c>reason</c>,
    /// <c>message_template_category</c>). UNVERIFIED that 360dialog forwards it to a channel's webhook URL (docs/WEBHOOKS.md).
    /// </summary>
    public static string TemplateStatusUpdate(string statusEvent = "APPROVED", string name = "recordatorio_cita", string language = "es", string category = "UTILITY", long time = T0) => $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "id": "102290129340398", "time": {{time}}, "changes": [ { "field": "message_template_status_update", "value": {
            "event": "{{statusEvent}}", "message_template_id": 594425479261596, "message_template_name": "{{name}}",
            "message_template_language": "{{language}}", "reason": "NONE", "message_template_category": "{{category}}"
          } } ] } ]
        }
        """;

    /// <summary>
    /// An image echo of the WhatsApp Business app, shaped after Meta's <c>smb_message_echoes</c> reference
    /// (https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/smb_message_echoes): the delivery carries
    /// <c>contacts[]</c> with <c>wa_id</c> and <c>user_id</c>, and the echo carries <c>to_user_id</c> and a media object with <c>caption</c>,
    /// <c>mime_type</c>, <c>sha256</c>, <c>id</c> and <c>url</c>.
    /// </summary>
    public static string EchoImageDocShaped(string id = "wamid.HBgM-ECHO-IMG-1", long timestamp = T0 + 90) => Envelope("smb_message_echoes", $$"""
        "contacts": [ { "wa_id": "{{CustomerWaId}}", "user_id": "CO.13491208655302741918" } ],
        "message_echoes": [ { "from": "{{BusinessNumber}}", "to": "{{CustomerWaId}}", "to_user_id": "CO.13491208655302741918", "id": "{{id}}",
          "timestamp": "{{timestamp}}", "type": "image",
          "image": { "caption": "Así queda la zona", "mime_type": "image/jpeg", "sha256": "3f9d94d399fa61c191bc1d4ca71375a035cd9b9f5b1128e1f0963a415c16b0cc", "id": "24230790383178626", "url": "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=24230790383178626" } } ]
        """);

    /// <summary>An echo of the business deleting (<c>revoke</c>) or editing (<c>edit</c>) a message it sent from the app (Meta's reference).</summary>
    public static string EchoRevokeOrEdit(string type, string id = "wamid.HBgM-ECHO-REV-1") => Envelope("smb_message_echoes", $$"""
        "contacts": [ { "wa_id": "{{CustomerWaId}}", "user_id": "CO.13491208655302741918" } ],
        "message_echoes": [ { "from": "{{BusinessNumber}}", "to": "{{CustomerWaId}}", "to_user_id": "CO.13491208655302741918", "id": "{{id}}",
          "timestamp": "{{T0 + 120}}", "type": "{{type}}",
          "{{type}}": { "original_message_id": "wamid.HBgM-ECHO-1"{{(type == "edit" ? ", \"message\": { \"type\": \"text\", \"text\": { \"body\": \"Corregido\" } }" : "")}} } } ]
        """);

    /// <summary>
    /// One chunk of the chat history the WhatsApp Business app shares after coexistence onboarding, shaped after Meta's <c>history</c> webhook
    /// reference (https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/history): a business text, a
    /// <c>media_placeholder</c> (no asset id) and a customer text, with <c>history_context.status</c>.
    /// </summary>
    public static string HistoryChunk(int phase = 1, int chunkOrder = 1, int progress = 55, string threadWaId = CustomerWaId, long timestamp = T0 - 86_400 * 30) => Envelope("history", $$"""
        "history": [ { "metadata": { "phase": {{phase}}, "chunk_order": {{chunkOrder}}, "progress": {{progress}} },
          "threads": [ { "id": "{{threadWaId}}", "context": { "wa_id": "{{threadWaId}}", "user_id": "CO.13491208655302741918" },
            "messages": [
              { "from": "{{BusinessNumber}}", "id": "wamid.HIST-OUT-1", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "Aquí tienes la información que pediste" }, "history_context": { "status": "READ" } },
              { "from": "{{BusinessNumber}}", "id": "wamid.HIST-OUT-2", "timestamp": "{{timestamp + 15}}", "type": "media_placeholder", "history_context": { "status": "PLAYED" } },
              { "from": "{{threadWaId}}", "from_user_id": "CO.13491208655302741918", "id": "wamid.HIST-IN-1", "timestamp": "{{timestamp + 15}}", "type": "text", "text": { "body": "¡Gracias!" }, "history_context": { "status": "READ" } }
            ] } ] } ]
        """);

    /// <summary>The follow-up of a history <c>media_placeholder</c>: a <c>messages[]</c> entry under the <c>history</c> field, with the asset id.</summary>
    public static string HistoryMediaFollowUp(string id = "wamid.HIST-IN-IMG-1", long timestamp = T0 - 86_400 * 3) => Envelope("history", $$"""
        "messages": [ { "from": "{{CustomerWaId}}", "from_user_id": "CO.13491208655302741918", "id": "{{id}}", "timestamp": "{{timestamp}}", "type": "image",
          "image": { "caption": "Foto de la zona", "mime_type": "image/jpeg", "sha256": "3f9d94d399fa61c191bc1d4ca71375a035cd9b9f5b1128e1f0963a415c16b0cc", "id": "24230790383178699" } } ]
        """);

    /// <summary>The business declined sharing its history (Meta's <c>history</c> reference: error <c>2593109</c>).</summary>
    public static string HistoryDeclined() => Envelope("history", """
        "history": [ { "errors": [ { "code": 2593109, "title": "History sync is turned off by the business from the WhatsApp Business App",
          "message": "History sync is turned off by the business from the WhatsApp Business App", "error_data": { "details": "History sharing is turned off by the business" } } ] } ]
        """);

    /// <summary>The phone's address book synced from the WhatsApp Business app (Meta's <c>smb_app_state_sync</c> reference). Never stored.</summary>
    public static string AppStateSync() => Envelope("smb_app_state_sync", $$"""
        "state_sync": [
          { "type": "contact", "contact": { "full_name": "Laura Gómez", "first_name": "Laura", "phone_number": "{{CustomerWaId}}", "user_id": "CO.13491208655302741918" }, "action": "add", "metadata": { "timestamp": "{{T0}}" } },
          { "type": "contact", "contact": { "phone_number": "573005550102", "user_id": "CO.99" }, "action": "remove", "metadata": { "timestamp": "{{T0}}" } }
        ]
        """);

    /// <summary>
    /// An <c>account_update</c> notification after Meta's reference
    /// (https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/account_update). With a
    /// <paramref name="reason"/>, a <c>PARTNER_REMOVED</c> of a coexistence number carries <c>disconnection_info</c>.
    /// </summary>
    public static string AccountUpdate(string accountEvent, string? reason = null, long time = T0) => $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "id": "2949482758682047", "time": {{time}}, "changes": [ { "field": "account_update", "value": {
            "event": "{{accountEvent}}",
            "waba_info": { "waba_id": "980198427658004", "owner_business_id": "2329417887457253" }{{(reason is null ? "" : $", \"disconnection_info\": {{ \"reason\": \"{reason}\", \"initiated_by\": \"SYSTEM\" }}")}}
          } } ] } ]
        }
        """;

    public static string UnknownEvent() => """
        { "object": "whatsapp_business_account", "entry": [ { "id": "1", "changes": [ { "field": "account_update", "value": { "event": "VERIFIED_ACCOUNT", "phone_number": "573005550100" } } ] } ] }
        """;
}
