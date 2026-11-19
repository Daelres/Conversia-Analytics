using System.Net;
using AngleSharp.Html.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Assistant;

/// <summary>The AI sales assistant's pages (docs/ASSISTANT.md) against the real API: settings, consent, lines, knowledge base, the admin's switch.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class AssistantPagesTests(TestApiFactory api)
{
    private const string SettingsPage = "/organizacion/asistente";
    private const string KnowledgePage = "/organizacion/asistente/conocimiento";

    private static IHtmlFormElement Form(PageResponse page, string testId)
        => (IHtmlFormElement)page.Document.QuerySelector($"form[data-testid={testId}]")!;

    [Fact]
    public async Task The_owner_switches_the_assistant_on_only_with_the_consent_and_it_reads_in_spanish()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "asistente");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(SettingsPage)).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("desactivado", page.Document.QuerySelector("[data-testid=assistant-state]")!.TextContent);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=assistant-generator-note]"));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));

        // Without ticking the boxes: refused, with the domain's words.
        var refused = await session.SubmitAsync(FormModel.From(page, Form(page, "assistant-switch-form")));
        refused.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("propietaria debe aceptar", refused.Text);

        foreach (var box in page.Document.QuerySelectorAll("form[data-testid=assistant-switch-form] input[type=checkbox]"))
        {
            box.SetAttribute("checked", "checked");
        }

        var enabled = await session.SubmitAsync(FormModel.From(page, Form(page, "assistant-switch-form")));
        var after = (await session.FollowAsync(enabled.ShouldRedirectTo($"{SettingsPage}?aviso=guardado"))).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("activado", after.Document.QuerySelector("[data-testid=assistant-state]")!.TextContent);
        Assert.NotNull(after.Document.QuerySelector("[data-testid=assistant-consent]"));
    }

    [Fact]
    public async Task The_behaviour_form_saves_and_shows_the_rule_of_a_wrong_value()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "asistente-ajustes");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = (await session.GetAsync(SettingsPage)).ShouldBe(HttpStatusCode.OK);

        var wrong = await session.SubmitAsync(FormModel.From(page, Form(page, "assistant-settings-form")).Set("ReplyDelaySeconds", "5"));
        wrong.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("entre 30 y 900 segundos", wrong.Text);

        var saved = await session.SubmitAsync(FormModel.From(page, Form(page, "assistant-settings-form")).Set("ReplyDelaySeconds", "120"));
        var after = (await session.FollowAsync(saved.ShouldRedirectTo($"{SettingsPage}?aviso=guardado"))).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("120", after.Document.QuerySelector("[data-testid=assistant-delay]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task A_member_without_the_permission_is_refused()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "asistente-sin");
        var member = await ApiSeed.CreateMemberAsync(api, owner, "miembro", Permission.ConversationsView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        (await session.GetAsync(SettingsPage)).ShouldBe(HttpStatusCode.Forbidden);
        (await session.GetAsync(KnowledgePage)).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_knowledge_base_saves_a_version_from_the_text_format_and_publishes_it()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "asistente-conocimiento");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = (await session.GetAsync(KnowledgePage)).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=knowledge-empty]"));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));

        var bad = await session.SubmitAsync(FormModel.From(page, Form(page, "knowledge-form")).Set("Services", "Axilas | | mucho"));
        bad.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("Servicios, línea 1", bad.Text);

        var saved = await session.SubmitAsync(FormModel.From(page, Form(page, "knowledge-form"))
            .Set("Services", "Axilas | Sesión corta | 60.000 | 20 | | activo")
            .Set("OpeningHours", "lunes | 08:00 | 18:00"));
        var after = (await session.FollowAsync(saved.ShouldRedirectTo($"{KnowledgePage}?aviso=guardado"))).ShouldBe(HttpStatusCode.OK);
        var version = Assert.Single(after.Select("[data-testid=knowledge-version]"));
        Assert.Equal("Draft", version.GetAttribute("data-status"));
        Assert.Contains("Axilas", ((IHtmlTextAreaElement)after.Document.QuerySelector("[data-testid=knowledge-services]")!).Value);

        var publishForm = FormModel.From(after, (IHtmlFormElement)after.Document.QuerySelector("[data-testid=knowledge-publish]")!.Closest("form")!);
        var published = await session.SubmitAsync(publishForm.With("Publish.Number", "1"));
        var final = (await session.FollowAsync(published.ShouldRedirectTo($"{KnowledgePage}?aviso=publicado"))).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Published", Assert.Single(final.Select("[data-testid=knowledge-version]")).GetAttribute("data-status"));
    }

    [Fact]
    public async Task The_administrator_stops_the_assistant_of_a_company_with_a_reason()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "asistente-admin");
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var url = $"/admin/empresas/{owner.TenantId}/asistente";
        var page = (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Permitido", page.Document.QuerySelector("[data-testid=assistant-company]")!.TextContent.Trim());

        var missing = await session.SubmitAsync(FormModel.From(page, Form(page, "admin-assistant-form")));
        missing.ShouldBe(HttpStatusCode.BadRequest);

        var stopped = await session.SubmitAsync(FormModel.From(page, Form(page, "admin-assistant-form")).Set("Reason", "Revisión de calidad"));
        var after = (await session.FollowAsync(stopped.ShouldRedirectTo($"{url}?aviso=guardado"))).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Detenido por el administrador", after.Document.QuerySelector("[data-testid=assistant-company]")!.TextContent.Trim());
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(after)));
    }
}

/// <summary>The conversation view with the assistant (stubbed API): its state, the reason of a handoff, take over and hand back.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class AssistantConversationViewTests
{
    private static string Url(Guid id) => $"/conversaciones/{id}";

    [Fact]
    public async Task A_handed_off_conversation_says_why_and_offers_to_hand_it_back()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Assistant = new Application.DTOs.Assistant.ConversationAssistantResponse
        {
            State = "HandedOff", HandoffReason = "human_requested", Available = true,
        });

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("HandedOff", page.Document.QuerySelector("[data-testid=assistant-card]")!.GetAttribute("data-assistant"));
        Assert.Contains("pidió hablar con una persona", page.Document.QuerySelector("[data-testid=assistant-reason]")!.TextContent);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=assistant-hand-back]"));
        Assert.Null(page.Document.QuerySelector("[data-testid=assistant-take-over]"));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
    }

    [Fact]
    public async Task Without_the_assistant_on_the_line_nothing_is_shown()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);

        Assert.Null(page.Document.QuerySelector("[data-testid=assistant-card]"));
        Assert.Null(page.Document.QuerySelector("[data-testid=assistant-take-over]"));
    }
}
