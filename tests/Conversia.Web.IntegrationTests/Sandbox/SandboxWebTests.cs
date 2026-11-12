extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Sandbox;

/// <summary>
/// The internal sandbox mode on the Web (docs/SANDBOX.md), against the real API and database: the administrator switches it with a
/// reason, the company sees the banner on every page, one person starts a simulated conversation and plays both sides, the badge marks
/// the sandbox data, and the clean-up asks before it deletes. The stand-in provider must stay untouched throughout.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class SandboxWebTests(TestApiFactory api)
{
    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    private static string? State(PageResponse page, string testId) => page.Document.QuerySelector($"[data-testid={testId}]")?.GetAttribute("data-state");

    private static bool Shown(PageResponse page, string selector)
        => page.Document.QuerySelector(selector) is { } element && !UiStructureAssertions.IsHidden(element);

    private async Task<(ApiSeed.Owner Owner, Guid Channel, MessagingApiHost Messaging, ApiSeed.Admin Admin)> StartAsync(string number, bool sandboxMode = true)
    {
        var messaging = api.WithMessaging();
        var owner = await ApiSeed.RegisterOwnerAsync(api, "sandbox");
        var channel = (await ApiSeed.CreateLinesAsync(api, owner, number))[0];
        await ApiSeed.ActivateLineAsync(api, owner, channel);
        var admin = await ApiSeed.CreateAdminAsync(api);
        if (sandboxMode)
        {
            await SetModeAsync(admin, owner.TenantId, true);
        }

        return (owner, channel, messaging, admin);
    }

    private async Task SetModeAsync(ApiSeed.Admin admin, Guid tenantId, bool enabled)
    {
        using var client = await ApiSeed.AdminClientAsync(api, admin);
        var response = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/sandbox-mode", new { enabled, reason = "Pruebas de la Web" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Runs the ingestion worker's runner by hand, as every ingestion test does: the Web tests' API has the hosted worker off.</summary>
    private static async Task IngestAsync(MessagingApiHost messaging)
    {
        var runner = messaging.Api.Services.GetRequiredService<WebhookEventRunner>();
        while (await runner.ProcessNextAsync())
        {
        }
    }

    private static async Task<List<Conversation>> ConversationsAsync(MessagingApiHost messaging, Guid tenantId)
    {
        using var scope = messaging.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Conversations.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.CreatedAt).ToListAsync();
    }

    private static async Task<PageResponse> StartSimulatedAsync(WebSession session, Guid channel, string name = "Laura Gómez", string text = "Hola, cuánto cuesta la depilación de axilas?")
    {
        var page = (await session.GetAsync("/conversaciones/simular")).ShouldBe(HttpStatusCode.OK);
        return await session.SubmitAsync(FormModel.From(page, Form(page, "sandbox-start-form")).Set("ChannelId", channel.ToString()).Set("CustomerName", name).Set("Text", text));
    }

    // ---- The administrator ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_administrator_switches_the_mode_from_the_company_page_with_a_required_reason_and_it_is_audited()
    {
        var (owner, _, _, admin) = await StartAsync("+57 300 888 0001", sandboxMode: false);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var company = $"/admin/empresas/{owner.TenantId}";

        var before = (await session.GetAsync(company)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("false", before.Document.QuerySelector("[data-testid=company-sandbox]")!.GetAttribute("data-enabled"));
        var link = before.Document.QuerySelector("[data-testid=sandbox-actions] a")!;
        Assert.Equal($"{company}/sandbox/activar", link.GetAttribute("href"));

        var confirm = (await session.GetAsync($"{company}/sandbox/activar")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Ningún mensaje de la empresa saldrá por 360dialog", confirm.Document.QuerySelector("[data-testid=confirm-effect]")!.Plain());

        // Without a reason nothing changes, and the page says which field is missing.
        var refused = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "sandbox-action-form")));
        Assert.False(refused.IsRedirect);
        Assert.Contains("Motivo", refused.Text);
        Assert.Equal("false", (await session.GetAsync(company)).Document.QuerySelector("[data-testid=company-sandbox]")!.GetAttribute("data-enabled"));

        var again = (await session.GetAsync($"{company}/sandbox/activar")).ShouldBe(HttpStatusCode.OK);
        var done = await session.SubmitAsync(FormModel.From(again, Form(again, "sandbox-action-form")).Set("Reason", "Demostración al cliente piloto"));

        done.ShouldRedirectTo($"{company}?aviso=sandbox-activo");
        var after = (await session.FollowAsync(done)).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Modo sandbox activado", after.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.Equal("true", after.Document.QuerySelector("[data-testid=company-sandbox]")!.GetAttribute("data-enabled"));
        Assert.Equal($"{company}/sandbox/desactivar", after.Document.QuerySelector("[data-testid=sandbox-actions] a")!.GetAttribute("href"));
        Assert.Contains("Activó el modo sandbox", after.Text);
        Assert.Contains("Motivo: Demostración al cliente piloto", after.Text);

        // The listing marks the company, and the action that no longer applies offers no form.
        var name = after.Document.QuerySelector("h1")!.Plain().Trim();
        var listing = (await session.GetAsync($"/admin?buscar={Uri.EscapeDataString(name)}")).ShouldBe(HttpStatusCode.OK);
        Assert.Single(listing.Select("[data-testid=company-sandbox]"));
        var stale = (await session.GetAsync($"{company}/sandbox/activar")).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(stale.Document.QuerySelector("[data-testid=not-allowed]"));
        Assert.False(Shown(stale, "[data-testid=sandbox-action-submit]"));

        var off = (await session.GetAsync($"{company}/sandbox/desactivar")).ShouldBe(HttpStatusCode.OK);
        (await session.SubmitAsync(FormModel.From(off, Form(off, "sandbox-action-form")).Set("Reason", "Fin de la demostración"))).ShouldRedirectTo($"{company}?aviso=sandbox-inactivo");
        var final = (await session.GetAsync(company)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("false", final.Document.QuerySelector("[data-testid=company-sandbox]")!.GetAttribute("data-enabled"));
        Assert.Contains("Desactivó el modo sandbox", final.Text);

        (await session.GetAsync($"{company}/sandbox/otra-cosa")).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_company_user_cannot_open_the_administrators_sandbox_page()
    {
        var (owner, _, _, _) = await StartAsync("+57 300 888 0002", sandboxMode: false);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var response = await session.GetAsync($"/admin/empresas/{owner.TenantId}/sandbox/activar");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("sandbox-action-form", response.Body);
    }

    // ---- The company: banner, both sides of a conversation, badge ----------------------------------------------------------

    [Fact]
    public async Task The_banner_is_on_every_page_of_a_company_in_sandbox_mode_and_on_none_outside_it()
    {
        var (owner, channel, messaging, admin) = await StartAsync("+57 300 888 0003", sandboxMode: false);
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        string[] pages = ["/", "/panel", "/conversaciones", "/conversaciones/simular", "/mensajes", "/mensajes/eventos", "/organizacion", $"/organizacion/canales/{channel}/clave-api", "/organizacion/sandbox/eliminar-datos", "/users"];

        foreach (var path in pages)
        {
            Assert.Empty((await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK).Select("[data-testid=sandbox-banner]"));
        }

        await SetModeAsync(admin, owner.TenantId, true);

        var problems = new List<string>();
        foreach (var path in pages)
        {
            var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);
            var banner = Assert.Single(page.Select("[data-testid=sandbox-banner]"));
            Assert.Contains("Modo sandbox activo", banner.Plain());
            Assert.Contains("no se envía ningún mensaje real", banner.Plain());
            problems.AddRange(PageStructureRules.Problems(path, page, true));
            problems.AddRange(SpanishUi.Leaks(SpanishUi.Extract(page)).Select(leak => $"{path}: {leak}"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task One_person_starts_a_simulated_conversation_and_plays_both_sides_without_a_provider_key()
    {
        var (owner, channel, messaging, _) = await StartAsync("+57 300 888 0004");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var list = (await session.GetAsync("/conversaciones")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("/conversaciones/simular", list.Document.QuerySelector("[data-testid=simulate-conversation]")!.GetAttribute("href"));

        var started = await StartSimulatedAsync(session, channel);
        started.ShouldRedirectTo("/conversaciones?notice=sandbox-started");
        Assert.Contains("Conversación simulada en cola", (await session.FollowAsync(started)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        await IngestAsync(messaging);

        var conversation = Assert.Single(await ConversationsAsync(messaging, owner.TenantId));
        Assert.True(conversation.IsSandbox);
        var row = Assert.Single((await session.GetAsync("/conversaciones")).Select("[data-testid=conversation-row]"));
        Assert.Equal("true", row.GetAttribute("data-sandbox"));
        Assert.Equal("Sandbox", row.QuerySelector("[data-testid=sandbox-chip]")!.Plain().Trim());

        // The detail: the badge, a composer that needs no key and shows no 360dialog sandbox note, and the second composer.
        var detail = (await session.GetAsync($"/conversaciones/{conversation.Id}")).ShouldBe(HttpStatusCode.OK);
        Assert.False((await messaging.LoadChannelAsync(channel)).HasProviderApiKey);
        Assert.Equal("Sandbox", detail.Document.QuerySelector("[data-testid=conversation-summary] [data-testid=sandbox-chip]")!.Plain().Trim());
        Assert.Equal("Available", State(detail, "composer"));
        Assert.Empty(detail.Select("[data-testid=composer-sandbox-note]"));
        Assert.Contains("no se envía a ningún teléfono", detail.Document.QuerySelector("[data-testid=composer-internal-note]")!.Plain());
        Assert.Equal("Available", State(detail, "customer-composer"));
        Assert.True(Shown(detail, "[data-testid=customer-composer-submit]"));
        Assert.Empty(PageStructureRules.Problems("sandbox detail", detail, true));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(detail)));

        var sent = await session.SubmitAsync(FormModel.From(detail, Form(detail, "composer-form")).Set("Text", "Son $60.000 la sesión."));
        sent.ShouldRedirectTo($"/conversaciones/{conversation.Id}?notice=sent");
        Assert.Empty(messaging.Sender.SentBy(channel)); // the provider's stand-in was never asked

        var afterAdvisor = (await session.FollowAsync(sent)).ShouldBe(HttpStatusCode.OK);
        var asCustomer = await session.SubmitAsync(FormModel.From(afterAdvisor, Form(afterAdvisor, "customer-composer-form")).Set("Text", "No gracias"));
        asCustomer.ShouldRedirectTo($"/conversaciones/{conversation.Id}?notice=customer-sent");
        Assert.Contains("Mensaje del cliente simulado en cola", (await session.FollowAsync(asCustomer)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        await IngestAsync(messaging);

        var thread = (await session.GetAsync($"/conversaciones/{conversation.Id}")).ShouldBe(HttpStatusCode.OK);
        var messages = thread.Select("[data-testid=chat-message]").ToList();
        Assert.Equal(["Inbound", "Outbound", "Inbound"], messages.Select(m => m.GetAttribute("data-direction")));
        Assert.Equal(
            ["Hola, cuánto cuesta la depilación de axilas?", "Son $60.000 la sesión.", "No gracias"],
            messages.Select(m => m.QuerySelector(".chat__text")!.Plain().Trim()));
        Assert.Single(await ConversationsAsync(messaging, owner.TenantId));
        Assert.Empty(messaging.Sender.SentBy(channel));
    }

    [Fact]
    public async Task The_two_composers_never_read_each_others_post_and_a_blank_customer_message_is_refused_on_its_own_form()
    {
        var (owner, channel, messaging, _) = await StartAsync("+57 300 888 0005");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await StartSimulatedAsync(session, channel)).ShouldRedirectTo("/conversaciones?notice=sandbox-started");
        await IngestAsync(messaging);
        var conversation = Assert.Single(await ConversationsAsync(messaging, owner.TenantId));
        var detail = (await session.GetAsync($"/conversaciones/{conversation.Id}")).ShouldBe(HttpStatusCode.OK);

        var advisor = FormModel.From(detail, Form(detail, "composer-form"));
        var customer = FormModel.From(detail, Form(detail, "customer-composer-form"));
        Assert.NotEqual(advisor.Get("_handler"), customer.Get("_handler"));

        var blank = await session.SubmitAsync(customer.Set("Text", "   "));

        Assert.False(blank.IsRedirect);
        Assert.Single(blank.Select("[data-testid=chat-message]")); // nothing was added, on either side
        Assert.Equal(string.Empty, blank.Document.QuerySelector("#composer-text")!.TextContent.Trim());
        using var scope = messaging.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.WebhookEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == owner.TenantId));
    }

    [Fact]
    public async Task Outside_sandbox_mode_a_sandbox_conversation_takes_no_message_and_offers_no_customer_and_a_forged_post_is_refused()
    {
        var (owner, channel, messaging, admin) = await StartAsync("+57 300 888 0006");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await StartSimulatedAsync(session, channel)).ShouldRedirectTo("/conversaciones?notice=sandbox-started");
        await IngestAsync(messaging);
        var sandbox = Assert.Single(await ConversationsAsync(messaging, owner.TenantId));
        var (real, _) = await messaging.SeedConversationAsync(channel);

        // Mode on: the real conversation is closed to writing, and has no simulated customer.
        var realPage = (await session.GetAsync($"/conversaciones/{real}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("SandboxModeActive", State(realPage, "composer"));
        Assert.Contains("Esta conversación es real", realPage.Document.QuerySelector("[data-testid=composer-unavailable]")!.Plain());
        Assert.Empty(realPage.Select("[data-testid=sandbox-chip]"));
        Assert.False(Shown(realPage, "[data-testid=customer-composer]"));
        Assert.False(Shown(realPage, "[data-testid=composer-submit]"));

        // Forms kept from the sandbox page while the mode was on...
        var whileOn = (await session.GetAsync($"/conversaciones/{sandbox.Id}")).ShouldBe(HttpStatusCode.OK);
        var staleCustomer = FormModel.From(whileOn, Form(whileOn, "customer-composer-form")).Set("Text", "mensaje-tardío-del-cliente");
        var staleAdvisor = FormModel.From(whileOn, Form(whileOn, "composer-form")).Set("Text", "mensaje-tardío-del-asesor");

        await SetModeAsync(admin, owner.TenantId, false);

        // ...and the page after it went off: marked, closed to writing, no simulated customer, no link to simulate.
        var page = (await session.GetAsync($"/conversaciones/{sandbox.Id}")).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=sandbox-chip]"));
        Assert.Equal("SandboxConversation", State(page, "composer"));
        Assert.False(Shown(page, "[data-testid=customer-composer]"));
        Assert.Empty((await session.GetAsync("/conversaciones")).Select("[data-testid=simulate-conversation]"));
        var simulate = (await session.GetAsync("/conversaciones/simular")).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(simulate.Document.QuerySelector("[data-testid=sandbox-off]"));
        Assert.False(Shown(simulate, "[data-testid=sandbox-start-submit]"));

        var refusedCustomer = await session.SubmitAsync(staleCustomer);
        Assert.Equal(HttpStatusCode.Conflict, refusedCustomer.StatusCode);
        Assert.Contains("El modo sandbox no está activo", refusedCustomer.Text);

        var refusedAdvisor = await session.SubmitAsync(staleAdvisor);
        Assert.Equal(HttpStatusCode.Conflict, refusedAdvisor.StatusCode);
        Assert.Contains("solo admite mensajes mientras el modo sandbox", refusedAdvisor.Text);

        await IngestAsync(messaging);
        var final = (await session.GetAsync($"/conversaciones/{sandbox.Id}")).ShouldBe(HttpStatusCode.OK);
        Assert.Single(final.Select("[data-testid=chat-message]"));
        Assert.Empty(messaging.Sender.SentBy(channel));
    }

    // ---- The diagnostic views show both kinds, each marked --------------------------------------------------------------------

    [Fact]
    public async Task The_messages_and_the_deliveries_pages_list_both_kinds_and_mark_the_sandbox_ones_in_both_modes()
    {
        var (owner, channel, messaging, admin) = await StartAsync("+57 300 888 0009");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await StartSimulatedAsync(session, channel, text: "mensaje-simulado")).ShouldRedirectTo("/conversaciones?notice=sandbox-started");
        await IngestAsync(messaging);
        var (real, _) = await messaging.SeedConversationAsync(channel);

        foreach (var mode in new[] { true, false })
        {
            await SetModeAsync(admin, owner.TenantId, mode);

            var feed = (await session.GetAsync("/mensajes")).ShouldBe(HttpStatusCode.OK);
            var items = feed.Select("[data-testid=feed-message]").ToList();
            Assert.Equal(2, items.Count);
            var simulated = Assert.Single(items, i => i.QuerySelector("[data-testid=feed-text]")!.Plain().Trim() == "mensaje-simulado");
            Assert.Equal("true", simulated.GetAttribute("data-sandbox"));
            Assert.Equal("Sandbox", simulated.QuerySelector("[data-testid=sandbox-chip]")!.Plain().Trim());
            var realItem = Assert.Single(items, i => i != simulated);
            Assert.Equal("false", realItem.GetAttribute("data-sandbox"));
            Assert.Null(realItem.QuerySelector("[data-testid=sandbox-chip]"));
            Assert.Contains(real.ToString(), realItem.QuerySelector("[data-testid=feed-conversation]")!.GetAttribute("href"));

            var deliveries = (await session.GetAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.OK);
            var row = Assert.Single(deliveries.Select("[data-testid=delivery-row]")); // the seeded real conversation came without a delivery
            Assert.Equal("true", row.GetAttribute("data-sandbox"));
            Assert.Equal("Sandbox", row.QuerySelector("[data-testid=sandbox-chip]")!.Plain().Trim());

            Assert.Empty(PageStructureRules.Problems("/mensajes", feed, true));
            Assert.Empty(PageStructureRules.Problems("/mensajes/eventos", deliveries, true));
        }
    }

    // ---- The clean-up ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sandbox_data_is_deleted_from_the_organization_page_after_a_confirmation_and_real_data_stays()
    {
        var (owner, channel, messaging, admin) = await StartAsync("+57 300 888 0007");
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await StartSimulatedAsync(session, channel)).ShouldRedirectTo("/conversaciones?notice=sandbox-started");
        await IngestAsync(messaging);
        var (real, _) = await messaging.SeedConversationAsync(channel);
        await SetModeAsync(admin, owner.TenantId, false); // cleaning up after the mode went off is the usual case

        var organization = (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("false", organization.Document.QuerySelector("[data-testid=sandbox-mode]")!.GetAttribute("data-enabled"));
        Assert.Equal("/organizacion/sandbox/eliminar-datos", organization.Document.QuerySelector("[data-testid=sandbox-delete-link]")!.GetAttribute("href"));

        // Opening the confirmation deletes nothing.
        var confirm = (await session.GetAsync("/organizacion/sandbox/eliminar-datos")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("no se puede deshacer", confirm.Document.QuerySelector("[data-testid=confirm-effect]")!.Plain());
        Assert.Equal(2, (await ConversationsAsync(messaging, owner.TenantId)).Count);
        Assert.Empty(PageStructureRules.Problems("sandbox delete", confirm, true));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(confirm)));

        var form = FormModel.From(confirm, Form(confirm, "sandbox-delete-form"));
        var done = await session.SubmitAsync(form);

        done.ShouldRedirectTo("/organizacion?notice=sandbox-deleted");
        Assert.Contains("Datos de prueba eliminados", (await session.FollowAsync(done)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.Equal(real, Assert.Single(await ConversationsAsync(messaging, owner.TenantId)).Id);
        var row = Assert.Single((await session.GetAsync("/conversaciones")).Select("[data-testid=conversation-row]"));
        Assert.Equal("false", row.GetAttribute("data-sandbox"));

        // The same form sent again does nothing and says so.
        var twice = await session.SubmitAsync(form);
        Assert.False(twice.IsRedirect);
        Assert.Contains("Este formulario ya se envió", twice.Text);
    }

    [Fact]
    public async Task The_clean_up_needs_the_permission_to_manage_the_organization()
    {
        var (owner, _, messaging, _) = await StartAsync("+57 300 888 0008");
        var member = await ApiSeed.CreateMemberAsync(api, owner, "visor", Permission.OrganizationView, Permission.ConversationsView);
        using var host = WebTestHost.Create(api, messaging.Api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        Assert.Empty((await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK).Select("[data-testid=sandbox-card]"));
        var page = await session.GetAsync("/organizacion/sandbox/eliminar-datos");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.False(Shown(page, "[data-testid=sandbox-delete-submit]"));

        // Nor may this person simulate a customer.
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/conversaciones/simular")).StatusCode);
        Assert.Empty((await session.GetAsync("/conversaciones")).Select("[data-testid=simulate-conversation]"));
    }

    // ---- The pure pieces ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, true, "Available")] // a real conversation, a key: as before
    [InlineData(false, false, false, "CredentialsMissing")] // ...and without a key: as before
    [InlineData(true, true, false, "Available")] // sandbox mode, sandbox conversation: no key needed
    [InlineData(true, false, true, "SandboxModeActive")]
    [InlineData(false, true, true, "SandboxConversation")]
    public void The_composer_follows_the_mode_and_the_kind_of_conversation(bool sandboxMode, bool sandboxConversation, bool hasKey, string expected)
    {
        var channel = new ConversationChannelState { IsActive = true, HasProviderApiKey = hasKey, ProviderEnvironment = "Sandbox" };

        var state = ComposerState.For(true, "Open", channel, sandboxMode, sandboxConversation);

        Assert.Equal(expected, state.Availability.ToString());
        Assert.Equal(sandboxMode && sandboxConversation, state.Internal);
        Assert.Equal(!sandboxMode && !sandboxConversation && hasKey, state.Sandbox); // the 360dialog sandbox note never shows in sandbox mode
        Assert.Equal(sandboxMode && sandboxConversation, CustomerComposer.For(true, "Open", channel, sandboxMode, sandboxConversation).CanSend);
    }

    [Fact]
    public void The_customer_composer_is_offered_only_to_a_person_who_may_write_on_an_open_conversation_of_an_active_line()
    {
        var on = new ConversationChannelState { IsActive = true };
        var off = new ConversationChannelState { IsActive = false };

        Assert.True(CustomerComposer.For(true, "Open", on, true, true).CanSend);
        Assert.False(CustomerComposer.For(false, "Open", on, true, true).CanSend);
        Assert.False(CustomerComposer.For(true, "Closed", on, true, true).CanSend);
        Assert.False(CustomerComposer.For(true, "Open", off, true, true).CanSend);
        Assert.Equal(ComposerAvailability.ChannelInactive, ComposerState.For(true, "Open", off, true, true).Availability);
    }

    [Fact]
    public void Every_refusal_of_the_simulated_customer_has_spanish_words_and_the_client_expects_exactly_those_codes()
    {
        var codes = Enum.GetValues<SandboxProblem>().Select(SandboxException.CodeOf).ToHashSet();

        Assert.Equal(codes.Order(), SandboxFailure.Codes.Order());
        foreach (var code in codes)
        {
            var message = SandboxFailure.Describe(code);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain(code, message);
            Assert.Empty(Conversia.TestSupport.EnglishLeaks.Find(message));
        }

        Assert.Null(SandboxFailure.Describe("something_else"));
    }

    [Theory]
    [InlineData(true, "Activó el modo sandbox")]
    [InlineData(false, "Desactivó el modo sandbox")]
    public void The_audit_trail_words_a_change_of_the_mode_with_its_reason(bool to, string title)
    {
        using var details = JsonDocument.Parse(JsonSerializer.Serialize(new { from = !to, to, reason = "Demo" }));
        var entry = new AuditEntryResponse { Action = PlatformAuditLog.Actions.TenantSandboxModeChanged, Details = details.RootElement.Clone() };

        var text = AuditFormat.Describe(entry);

        Assert.Equal((title, "Motivo: Demo"), (text.Title, text.Detail));
    }
}
