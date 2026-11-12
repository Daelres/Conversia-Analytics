using System.Net;
using AngleSharp.Html.Dom;
using Conversia.Application.Messaging;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Messaging;

/// <summary>
/// The two messaging screens against the real API and database, with a stand-in provider: the key a person types is stored
/// (encrypted) and never comes back, and a message a person writes is sent, stored and shown. What the real API answers is what
/// the pages read; nothing here calls 360dialog.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class MessagingEndToEndTests(TestApiFactory api)
{
    /// <summary>A dummy key in the shape the API accepts.</summary>
    private const string Key = "d360-dummy-key-Rt5Yw8Kc3";

    private const string NewKey = "d360-dummy-key-Jn2Fd6Xs7";

    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    private static AngleSharp.Dom.IElement KeyCell(PageResponse page, Guid channel) => page.Document.QuerySelector($"[data-testid=channel-row][data-channel-id='{channel}'] [data-testid=channel-key]")!;

    private async Task<(ApiSeed.Owner Owner, Guid Channel, MessagingApiHost Messaging)> StartLineAsync(string number, bool active = true)
    {
        var messaging = api.WithMessaging();
        var owner = await ApiSeed.RegisterOwnerAsync(api, "mensajes");
        var channel = (await ApiSeed.CreateLinesAsync(api, owner, number))[0];
        if (active)
        {
            await ApiSeed.ActivateLineAsync(api, owner, channel);
        }

        return (owner, channel, messaging);
    }

    private static async Task SaveKeyAsync(WebSession session, Guid channel, string? key, string environment)
    {
        var page = (await session.GetAsync($"/organizacion/canales/{channel}/clave-api")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, Form(page, "credentials-form")).Set("Environment", environment);
        if (key is not null)
        {
            form.Set("ApiKey", key);
        }

        Assert.True((await session.SubmitAsync(form)).IsRedirect);
    }

    [Fact]
    public async Task A_key_is_set_replaced_and_removed_from_the_web_and_is_never_shown_again()
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0001");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        Assert.Contains("Sin clave", KeyCell(await session.GetAsync("/organizacion"), channel).Plain());

        await SaveKeyAsync(session, channel, Key, "Sandbox");
        var stored = await messaging.LoadChannelAsync(channel);
        Assert.True(stored.HasProviderApiKey);
        Assert.Equal(WhatsAppProviderEnvironment.Sandbox, stored.ProviderEnvironment);
        var afterSet = KeyCell(await session.GetAsync("/organizacion"), channel);
        Assert.Contains("Configurada", afterSet.Plain());
        Assert.Contains("Sandbox (pruebas)", afterSet.Plain());

        await SaveKeyAsync(session, channel, NewKey, "Production");
        var replaced = await messaging.LoadChannelAsync(channel);
        Assert.NotEqual(stored.ProviderApiKeyCiphertext, replaced.ProviderApiKeyCiphertext);
        Assert.Contains("Producción", KeyCell(await session.GetAsync("/organizacion"), channel).Plain());

        await SaveKeyAsync(session, channel, key: null, "Sandbox"); // the environment alone: the stored key stays
        var kept = await messaging.LoadChannelAsync(channel);
        Assert.Equal(replaced.ProviderApiKeyCiphertext, kept.ProviderApiKeyCiphertext);
        Assert.Equal(WhatsAppProviderEnvironment.Sandbox, kept.ProviderEnvironment);

        var confirm = (await session.GetAsync($"/organizacion/canales/{channel}/quitar-clave")).ShouldBe(HttpStatusCode.OK);
        (await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "channel-action-form")))).ShouldRedirectTo("/organizacion?notice=key-removed");
        Assert.False((await messaging.LoadChannelAsync(channel)).HasProviderApiKey);
        Assert.Contains("Sin clave", KeyCell(await session.GetAsync("/organizacion"), channel).Plain());

        await session.GetAsync($"/organizacion/canales/{channel}/clave-api");
        Assert.All(new[] { Key, NewKey }, key =>
        {
            Assert.All(session.Transcript, response => Assert.DoesNotContain(key, response.Body));
            Assert.DoesNotContain(key, string.Join("\n", session.Transcript.Select(r => r.RequestUri + " " + r.Header("Location"))));
            Assert.DoesNotContain(session.Transcript.SelectMany(r => r.SetCookies), cookie => cookie.Contains(key));
            Assert.Empty(host.Logs.Mentioning(key));
        });
    }

    [Fact]
    public async Task A_message_written_on_the_page_is_sent_to_the_customer_and_shown_in_the_thread()
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0002");
        var (conversation, waId) = await messaging.SeedConversationAsync(channel);
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var before = (await session.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("CredentialsMissing", before.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));

        await SaveKeyAsync(session, channel, Key, "Sandbox");
        var page = (await session.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Available", page.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));
        Assert.Contains("25 solicitudes en total", page.Document.QuerySelector("[data-testid=composer-sandbox-note]")!.Plain());

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "composer-form")).Set("Text", "Hola, con gusto te ayudo."));

        response.ShouldRedirectTo($"/conversaciones/{conversation}?notice=sent");
        Assert.Equal((channel, waId, "Hola, con gusto te ayudo."), Assert.Single(messaging.Sender.SentBy(channel)));
        var thread = (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        var messages = thread.Select("[data-testid=chat-message]").ToList();
        Assert.Equal(["Inbound", "Outbound"], messages.Select(m => m.GetAttribute("data-direction")));
        Assert.Equal("Hola, con gusto te ayudo.", messages[1].QuerySelector(".chat__text")!.Plain().Trim());
        Assert.Empty(host.Logs.Mentioning(Key));
    }

    [Theory]
    [InlineData(WhatsAppSendStatus.LimitReached, 429, "25 solicitudes en total")]
    [InlineData(WhatsAppSendStatus.TransientFailure, 503, "no se sabe si el mensaje salió")]
    [InlineData(WhatsAppSendStatus.RecipientNotAllowed, 422, "no permite escribirle a este cliente")]
    [InlineData(WhatsAppSendStatus.InvalidCredentials, 409, "no reconoce la clave de API")]
    [InlineData(WhatsAppSendStatus.Rejected, 422, "no aceptó el mensaje")]
    public async Task A_refusal_of_the_provider_comes_through_the_real_api_as_its_spanish_message(WhatsAppSendStatus failure, int status, string expected)
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0003");
        var (conversation, _) = await messaging.SeedConversationAsync(channel);
        messaging.Sender.Answer(channel, () => WhatsAppSendResult.Failed(failure));
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        await SaveKeyAsync(session, channel, Key, "Sandbox");
        var page = await session.GetAsync($"/conversaciones/{conversation}");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "composer-form")).Set("Text", "Hola"));

        response.ShouldBe((HttpStatusCode)status);
        Assert.Contains(expected, response.Document.QuerySelector("[data-testid=composer-error]")!.Plain());
        Assert.Equal("Hola", ((IHtmlTextAreaElement)response.Document.QuerySelector("[data-testid=composer] textarea")!).Value);
        Assert.Single(response.Select("[data-testid=chat-message]")); // nothing was stored
        Assert.Empty(response.Select(ErrorCodeAssertions.Selector));
    }

    [Fact]
    public async Task The_real_api_refuses_a_closed_conversation_an_inactive_line_and_a_line_without_a_key_and_the_page_says_each()
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0004", active: false);
        var (open, _) = await messaging.SeedConversationAsync(channel);
        var (closed, _) = await messaging.SeedConversationAsync(channel, closed: true);
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        async Task<PageResponse> ForgeAsync(Guid conversation)
        {
            var page = (await session.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
            Assert.Empty(page.Select("[data-testid=composer] button[type=submit]")); // not offered: the post below is forged
            return await session.SubmitAsync(FormModel.From(page, Form(page, "composer-form")).Set("Text", "Hola"));
        }

        var onClosed = (await ForgeAsync(closed)).ShouldBe(HttpStatusCode.Conflict);
        Assert.Contains("está cerrada", onClosed.Document.QuerySelector("[data-testid=composer-error]")!.Plain());

        var onInactive = (await ForgeAsync(open)).ShouldBe(HttpStatusCode.Conflict);
        Assert.Contains("desactivado", onInactive.Document.QuerySelector("[data-testid=composer-error]")!.Plain());

        await ApiSeed.ActivateLineAsync(api, owner, channel);
        var withoutKey = (await ForgeAsync(open)).ShouldBe(HttpStatusCode.Conflict);
        Assert.Contains("no tiene clave de API", withoutKey.Document.QuerySelector("[data-testid=composer-error]")!.Plain());
        Assert.Empty(messaging.Sender.SentBy(channel));
    }

    [Fact]
    public async Task A_member_who_manages_conversations_but_not_the_organization_is_told_why_a_line_cannot_send()
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0006", active: false);
        var (conversation, _) = await messaging.SeedConversationAsync(channel);
        var manager = await ApiSeed.CreateMemberAsync(api, owner, "gestora", Permission.ConversationsView, Permission.ConversationsManage);
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, manager.Email);

        var inactive = (await session.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("ChannelInactive", inactive.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));

        await ApiSeed.ActivateLineAsync(api, owner, channel);
        var withoutKey = (await session.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("CredentialsMissing", withoutKey.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));
        Assert.Contains("no tiene clave de API", withoutKey.Document.QuerySelector("[data-testid=composer-unavailable]")!.Plain());
    }

    [Fact]
    public async Task A_member_who_manages_conversations_but_not_the_organization_can_send_and_one_who_only_views_cannot()
    {
        var (owner, channel, messaging) = await StartLineAsync("+57 300 777 0005");
        var (conversation, _) = await messaging.SeedConversationAsync(channel);
        var manager = await ApiSeed.CreateMemberAsync(api, owner, "gestora", Permission.ConversationsView, Permission.ConversationsManage);
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "lectora", Permission.ConversationsView);
        using var host = WebTestHost.Create(api, messaging.Api);
        using (var ownerSession = await WebFlows.SignedInAsync(host, owner.Email))
        {
            await SaveKeyAsync(ownerSession, channel, Key, "Sandbox");
        }

        using var viewing = await WebFlows.SignedInAsync(host, viewer.Email);
        var viewed = (await viewing.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("NoPermission", viewed.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));
        (await viewing.SubmitAsync(FormModel.From(viewed, Form(viewed, "composer-form")).Set("Text", "Hola"))).ShouldBe(HttpStatusCode.Forbidden);
        Assert.Empty(messaging.Sender.SentBy(channel));

        using var managing = await WebFlows.SignedInAsync(host, manager.Email);
        var page = (await managing.GetAsync($"/conversaciones/{conversation}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Available", page.Document.QuerySelector("[data-testid=composer]")!.GetAttribute("data-state"));
        Assert.Contains("25 solicitudes en total", page.Document.QuerySelector("[data-testid=composer-sandbox-note]")!.Plain()); // without OrganizationView
        Assert.Empty(page.Select(ErrorCodeAssertions.Selector));
        Assert.True((await managing.SubmitAsync(FormModel.From(page, Form(page, "composer-form")).Set("Text", "Hola"))).IsRedirect);
        Assert.Single(messaging.Sender.SentBy(channel));
    }
}
