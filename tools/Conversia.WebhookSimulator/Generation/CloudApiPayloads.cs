using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conversia.WebhookSimulator.Generation;

/// <summary>The business number a delivery is about, as 360dialog reports it in <c>metadata</c>.</summary>
/// <param name="BusinessNumber">The digits of the number of the business (<c>display_phone_number</c>).</param>
/// <param name="PhoneNumberId">The provider's id of that number (<c>phone_number_id</c>).</param>
public sealed record ChannelIdentity(string BusinessNumber, string PhoneNumberId);

/// <param name="Id">The provider's message id (<c>wamid...</c>): unique per channel.</param>
/// <param name="UnixSeconds">When the message was sent, in unix seconds.</param>
/// <param name="Type"><c>text</c>, or <c>image</c> for a picture without text.</param>
public sealed record DeliveryMessage(string Id, string? Text, long UnixSeconds, string Type = "text");

/// <summary>The click-to-WhatsApp ad a customer came from.</summary>
public sealed record AdReferral(string SourceUrl, string SourceId, string Headline);

/// <summary>
/// Builds the bodies 360dialog would POST to the webhook, in the shapes <c>Dialog360PayloadParser</c> reads (docs/WEBHOOKS.md): the WhatsApp
/// Cloud API envelope (<c>entry[].changes[].value</c>), customers' messages in the <c>messages</c> field and the messages the business sends
/// from the WhatsApp Business app in <c>smb_message_echoes</c>.
/// <para>The echo shape follows the public documentation and has NOT been verified against a real 360dialog sandbox, the same open question
/// as the parser's: a green simulator run proves the pipeline, not what the real provider sends.</para>
/// </summary>
public static class CloudApiPayloads
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Messages written by one customer, with their profile name; <paramref name="referral"/> goes on the first one (an ad opens a chat).</summary>
    public static string Inbound(ChannelIdentity channel, string waId, string name, IEnumerable<DeliveryMessage> messages, AdReferral? referral = null)
        => Serialize(Envelope(channel, MessagesChange(channel, waId, name, messages, referral)));

    /// <summary>Messages the business sent to one customer from the WhatsApp Business app (coexistence echoes). Unverified shape.</summary>
    public static string Echo(ChannelIdentity channel, string toWaId, IEnumerable<DeliveryMessage> messages)
        => Serialize(Envelope(channel, EchoChange(channel, toWaId, messages)));

    /// <summary>A whole chat in one delivery: the customer's messages and the business echoes as two changes of the same entry.</summary>
    public static string Conversation(
        ChannelIdentity channel, string waId, string name, IEnumerable<DeliveryMessage> inbound, IEnumerable<DeliveryMessage> echoes, AdReferral? referral = null)
    {
        var changes = new List<JsonNode>();
        var inboundList = inbound.ToList();
        var echoList = echoes.ToList();
        if (inboundList.Count > 0)
        {
            changes.Add(MessagesChange(channel, waId, name, inboundList, referral));
        }

        if (echoList.Count > 0)
        {
            changes.Add(EchoChange(channel, waId, echoList));
        }

        return Serialize(Envelope(channel, changes.ToArray()));
    }

    /// <summary>
    /// A customer identified by a business-scoped user id (<c>CO.1349...</c>) instead of a phone number. The product cannot key a contact by it
    /// yet, so ingestion skips and counts these messages (docs/WEBHOOKS.md): this is the payload that proves it.
    /// </summary>
    public static string UserWithoutPhone(ChannelIdentity channel, string userId, string name, DeliveryMessage message)
    {
        var value = ValueOf(channel);
        value["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = name }, ["wa_id"] = userId, ["user_id"] = userId });
        value["messages"] = new JsonArray(new JsonObject
        {
            ["from"] = userId,
            ["from_user_id"] = userId,
            ["id"] = message.Id,
            ["timestamp"] = message.UnixSeconds.ToString(CultureInfo.InvariantCulture),
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = message.Text },
        });
        return Serialize(Envelope(channel, Change("messages", value)));
    }

    /// <summary>The same delivery with an unrelated field added: another body (so another hash) around the same messages.</summary>
    public static string WithExtraField(string body)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root["simulator_note"] = "same messages, different body";
        return Serialize(root);
    }

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static JsonObject MessagesChange(ChannelIdentity channel, string waId, string name, IEnumerable<DeliveryMessage> messages, AdReferral? referral)
    {
        var value = ValueOf(channel);
        value["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = name }, ["wa_id"] = waId });
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject
            {
                ["from"] = waId,
                ["id"] = message.Id,
                ["timestamp"] = message.UnixSeconds.ToString(CultureInfo.InvariantCulture),
                ["type"] = message.Type,
            };
            if (message.Type == "text")
            {
                node["text"] = new JsonObject { ["body"] = message.Text };
            }
            else
            {
                node[message.Type] = new JsonObject { ["id"] = "sim-media-" + message.Id, ["mime_type"] = "image/jpeg", ["sha256"] = "sim" };
            }

            if (referral is not null && array.Count == 0)
            {
                node["referral"] = new JsonObject
                {
                    ["source_url"] = referral.SourceUrl,
                    ["source_id"] = referral.SourceId,
                    ["source_type"] = "ad",
                    ["headline"] = referral.Headline,
                    ["body"] = "Agenda hoy",
                    ["media_type"] = "image",
                };
            }

            array.Add(node);
        }

        value["messages"] = array;
        return Change("messages", value);
    }

    private static JsonObject EchoChange(ChannelIdentity channel, string toWaId, IEnumerable<DeliveryMessage> messages)
    {
        var value = ValueOf(channel);
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(new JsonObject
            {
                ["from"] = channel.BusinessNumber,
                ["to"] = toWaId,
                ["id"] = message.Id,
                ["timestamp"] = message.UnixSeconds.ToString(CultureInfo.InvariantCulture),
                ["type"] = "text",
                ["text"] = new JsonObject { ["body"] = message.Text },
            });
        }

        value["message_echoes"] = array;
        return Change("smb_message_echoes", value);
    }

    private static JsonObject ValueOf(ChannelIdentity channel) => new()
    {
        ["messaging_product"] = "whatsapp",
        ["metadata"] = new JsonObject { ["display_phone_number"] = channel.BusinessNumber, ["phone_number_id"] = channel.PhoneNumberId },
    };

    private static JsonObject Change(string field, JsonObject value) => new() { ["field"] = field, ["value"] = value };

    private static JsonObject Envelope(ChannelIdentity channel, params JsonNode[] changes)
    {
        var array = new JsonArray();
        foreach (var change in changes)
        {
            array.Add(change);
        }

        return new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject { ["id"] = "sim-" + channel.PhoneNumberId, ["changes"] = array }),
        };
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(Options);
}
