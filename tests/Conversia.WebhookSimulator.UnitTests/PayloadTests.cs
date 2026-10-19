using System.Text.Json;
using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

/// <summary>The bodies the simulator POSTs have the shape of a WhatsApp Cloud API delivery, and the product's own parser reads them as intended.</summary>
public class PayloadTests
{
    private static readonly ChannelIdentity Channel = new("573005550101", "106540352242922");

    private static DateTime Now => TimeProvider.System.GetUtcNow().UtcDateTime;

    private static DeliveryMessage Message(string id, string text, DateTime at) => new(id, text, CloudApiPayloads.ToUnix(at));

    [Fact]
    public void An_inbound_delivery_has_the_cloud_api_envelope()
    {
        var at = Now.AddMinutes(-10);

        var json = CloudApiPayloads.Inbound(Channel, "573005550999", "Laura Gómez", [Message("wamid.SIM1", "Hola, quiero información", at)]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("whatsapp_business_account", root.GetProperty("object").GetString());
        var change = root.GetProperty("entry")[0].GetProperty("changes")[0];
        Assert.Equal("messages", change.GetProperty("field").GetString());
        var value = change.GetProperty("value");
        Assert.Equal("whatsapp", value.GetProperty("messaging_product").GetString());
        Assert.Equal(Channel.BusinessNumber, value.GetProperty("metadata").GetProperty("display_phone_number").GetString());
        Assert.Equal(Channel.PhoneNumberId, value.GetProperty("metadata").GetProperty("phone_number_id").GetString());
        Assert.Equal("573005550999", value.GetProperty("contacts")[0].GetProperty("wa_id").GetString());
        Assert.Equal("Laura Gómez", value.GetProperty("contacts")[0].GetProperty("profile").GetProperty("name").GetString());
        var message = value.GetProperty("messages")[0];
        Assert.Equal("573005550999", message.GetProperty("from").GetString());
        Assert.Equal("wamid.SIM1", message.GetProperty("id").GetString());
        Assert.Equal("text", message.GetProperty("type").GetString());
        Assert.Equal("Hola, quiero información", message.GetProperty("text").GetProperty("body").GetString());
        // Unix seconds, written as text the way the Cloud API does.
        Assert.Equal(JsonValueKind.String, message.GetProperty("timestamp").ValueKind);
        Assert.Equal(CloudApiPayloads.ToUnix(at), long.Parse(message.GetProperty("timestamp").GetString()!));
    }

    [Fact]
    public void Accents_stay_readable_in_the_body_and_the_text_is_never_html_escaped_into_something_else()
    {
        var json = CloudApiPayloads.Inbound(Channel, "573005550999", "Ángela", [Message("wamid.SIM1", "¿Cuánto cuesta <la> sesión?", Now.AddMinutes(-5))]);

        Assert.Contains("¿Cuánto", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("¿Cuánto cuesta <la> sesión?", document.RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value").GetProperty("messages")[0].GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public void A_business_echo_comes_from_the_business_number_to_the_customer()
    {
        var json = CloudApiPayloads.Echo(Channel, "573005550999", [Message("wamid.SIM2", "¡Hola! Con gusto te ayudo.", Now.AddMinutes(-8))]);

        using var document = JsonDocument.Parse(json);
        var change = document.RootElement.GetProperty("entry")[0].GetProperty("changes")[0];
        Assert.Equal("smb_message_echoes", change.GetProperty("field").GetString());
        var echo = change.GetProperty("value").GetProperty("message_echoes")[0];
        Assert.Equal(Channel.BusinessNumber, echo.GetProperty("from").GetString());
        Assert.Equal("573005550999", echo.GetProperty("to").GetString());
        Assert.Equal("wamid.SIM2", echo.GetProperty("id").GetString());
        Assert.Equal("¡Hola! Con gusto te ayudo.", echo.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public void An_ad_referral_goes_on_the_first_message_only()
    {
        var referral = new AdReferral("https://fb.me/sim-ad-1", "120200000000001", "Depilación láser 2x1 en axilas");

        var json = CloudApiPayloads.Inbound(Channel, "573005550999", "Laura", [Message("wamid.A", "Hola, vi su anuncio", Now.AddMinutes(-9)), Message("wamid.B", "¿Cuánto cuesta?", Now.AddMinutes(-8))], referral);

        using var document = JsonDocument.Parse(json);
        var messages = document.RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value").GetProperty("messages");
        Assert.Equal("Depilación láser 2x1 en axilas", messages[0].GetProperty("referral").GetProperty("headline").GetString());
        Assert.False(messages[1].TryGetProperty("referral", out _));
    }

    [Fact]
    public void A_whole_chat_in_one_delivery_has_a_change_for_the_customer_and_one_for_the_advisor()
    {
        var json = CloudApiPayloads.Conversation(
            Channel, "573005550999", "Laura", [Message("wamid.A", "Hola", Now.AddMinutes(-9))], [Message("wamid.B", "Buenas", Now.AddMinutes(-8))]);

        using var document = JsonDocument.Parse(json);
        var changes = document.RootElement.GetProperty("entry")[0].GetProperty("changes");
        Assert.Equal(["messages", "smb_message_echoes"], changes.EnumerateArray().Select(c => c.GetProperty("field").GetString()));
    }

    [Fact]
    public void The_product_parser_reads_what_the_simulator_sends_and_tells_customer_from_business()
    {
        var at = Now.AddMinutes(-30);
        var referral = new AdReferral("https://fb.me/sim-ad-1", "120200000000001", "Depilación láser 2x1");
        var json = CloudApiPayloads.Conversation(
            Channel, "573005550999", "Laura Gómez", [Message("wamid.A", "Hola, vi su anuncio", at)], [Message("wamid.B", "¡Hola Laura!", at.AddMinutes(3))], referral);

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Equal(0, parsed.SkippedCount);
        Assert.Equal(2, parsed.Messages.Count);
        var customer = parsed.Messages.Single(m => m.Direction == MessageDirection.Inbound);
        var advisor = parsed.Messages.Single(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal("573005550999", customer.ContactWaId);
        Assert.Equal("573005550999", advisor.ContactWaId);
        Assert.Equal("Laura Gómez", customer.ContactName);
        Assert.Equal("Hola, vi su anuncio", customer.Text);
        Assert.Equal("¡Hola Laura!", advisor.Text);
        Assert.Equal("Depilación láser 2x1", customer.Referral!.Reference);
        Assert.Equal(CloudApiPayloads.ToUnix(at), new DateTimeOffset(customer.SentAt, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal(Channel.PhoneNumberId, customer.PhoneNumberId);
    }

    [Fact]
    public void A_customer_known_only_by_a_business_scoped_id_is_skipped_and_counted_never_stored_under_a_fake_number()
    {
        var json = CloudApiPayloads.UserWithoutPhone(Channel, "CO.13491208655302741918", "Laura", Message("wamid.U", "Hola", Now.AddMinutes(-5)));

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    [Fact]
    public void The_same_message_in_another_body_is_a_different_body_with_the_same_message_id()
    {
        var body = CloudApiPayloads.Inbound(Channel, "573005550999", "Laura", [Message("wamid.D", "Hola", Now.AddMinutes(-5))]);

        var other = CloudApiPayloads.WithExtraField(body);

        Assert.NotEqual(body, other);
        Assert.Equal("wamid.D", Dialog360PayloadParser.Parse(other).Messages.Single().ProviderMessageId);
        Assert.Equal(Dialog360PayloadParser.Parse(body).Messages.Single().ProviderMessageId, Dialog360PayloadParser.Parse(other).Messages.Single().ProviderMessageId);
    }

    [Fact]
    public void A_picture_without_text_has_no_body()
    {
        var json = CloudApiPayloads.Inbound(Channel, "573005550999", "Laura", [new DeliveryMessage("wamid.I", null, CloudApiPayloads.ToUnix(Now.AddMinutes(-5)), "image")]);

        var message = Dialog360PayloadParser.Parse(json).Messages.Single();

        Assert.Equal("image", message.Type);
        Assert.Null(message.Text);
    }
}
