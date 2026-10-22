extern alias ApiHost;

using System.Net;
using Conversia.Application.DTOs.Tenants;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Organization;

/// <summary>
/// The lines of a company and its quota on the organization page: "Líneas activas X de Y", the state of each line, the activation that the quota
/// refuses (the API's own message, plus who can help) and the creation form's promise. The states are put in place with a stand-in API; the
/// whole flow, with the real quota rules, runs against the real API at the end.
/// </summary>
public class OrganizationQuotaTests
{
    private static TenantInfoResponse Tenant(int active, int max, int registered = 2, TenantStatus status = TenantStatus.Active)
        => new() { Id = FakeBackend.TenantId, Name = "Empresa de prueba", Status = status, ActiveLines = active, MaxActiveLines = max, RegisteredLines = registered };

    private static string Problem(int status, string detail) => $$"""{"status":{{status}},"title":"x","detail":"{{detail}}"}""";

    // ---- the numbers ----

    [Theory]
    [InlineData(1, 3, "open", "2 disponibles")]
    [InlineData(2, 3, "open", "1 disponible")]
    [InlineData(3, 3, "full", "Cupo completo")]
    [InlineData(0, 0, "none", "Sin cupo asignado")]
    public async Task The_channels_card_shows_active_lines_of_the_quota_in_words_and_in_a_chip(int active, int max, string state, string chip)
    {
        using var harness = OrganizationHarness.Create(configure: stub => stub.Tenant = Tenant(active, max));

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var quota = page.Document.QuerySelector("[data-testid=channels-card] [data-testid=line-quota]")!;
        Assert.Equal($"Líneas activas {active} de {max}", quota.QuerySelector(".quota__title")!.Plain().Trim());
        Assert.Equal(state, quota.GetAttribute("data-state"));
        Assert.Equal(chip, quota.QuerySelector("[data-testid=quota-chip]")!.Plain().Trim());
        Assert.Equal($"{active} de {max} líneas activas", quota.QuerySelector("svg.barlist__bar")!.GetAttribute("aria-label")); // the bar is named; the text carries the data
    }

    [Fact]
    public async Task A_company_with_no_quota_is_told_why_and_that_it_can_register_its_numbers()
    {
        using var harness = OrganizationHarness.Create(configure: stub => stub.Tenant = Tenant(0, 0));

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var note = page.Document.QuerySelector("[data-testid=quota-note]")!.Plain();
        Assert.Contains("todavía no tiene líneas activas asignadas", note);
        Assert.Contains("acuerdo comercial", note);
        Assert.Contains("Puedes registrar tus números desde ya", note);
    }

    [Fact]
    public async Task A_company_that_uses_its_whole_quota_is_told_how_to_get_another_line()
    {
        using var harness = OrganizationHarness.Create(configure: stub => stub.Tenant = Tenant(2, 2));

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("ampliar tu cupo", page.Document.QuerySelector("[data-testid=quota-note]")!.Plain());
    }

    [Fact]
    public async Task A_company_with_room_left_gets_no_warning()
    {
        using var harness = OrganizationHarness.Create(configure: stub => stub.Tenant = Tenant(1, 3));

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("[data-testid=quota-note]"));
        Assert.Empty(page.Select("[data-testid=channels-card] .alert"));
    }

    // ---- the lines ----

    [Fact]
    public async Task Each_line_says_if_it_is_active_and_offers_the_action_that_changes_it()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var rows = page.Select("[data-testid=channel-row]").ToList();
        Assert.Equal(["Activa", "Inactiva"], rows.Select(r => r.QuerySelector("[data-testid=channel-state]")!.Plain().Trim()));
        Assert.Equal(["Desactivar", "Activar línea"], rows.Select(r => r.QuerySelector("a[href*='/activar'], a[href*='/desactivar']")!.Plain().Trim()));
        Assert.Contains(rows[0].QuerySelectorAll("a").Select(a => a.GetAttribute("href")), h => h == $"/organizacion/canales/{OrganizationStub.Line}/desactivar");
        Assert.Contains(rows[1].QuerySelectorAll("a").Select(a => a.GetAttribute("href")), h => h == $"/organizacion/canales/{OrganizationStub.Retired}/activar");
    }

    [Fact]
    public async Task The_creation_form_promises_nothing_the_quota_does_not_allow()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var intro = page.Document.QuerySelector("[data-testid=channel-form-intro]")!.Plain();
        Assert.Contains("Registra el número de WhatsApp", intro);
        Assert.Contains("Para recibir mensajes, la línea debe estar activa", intro);
        Assert.Contains("el número de líneas activas lo define tu acuerdo con Conversia", intro);
        Assert.Contains("Registrar número", page.Document.QuerySelector("[data-testid=channel-form]")!.Plain());
    }

    // ---- activating a line ----

    [Fact]
    public async Task A_full_quota_refuses_the_activation_with_the_apis_message_and_a_way_to_get_help()
    {
        const string message = "Tu cupo de líneas activas está completo (2 de 2). Contacta al administrador de Conversia.";
        using var harness = OrganizationHarness.Create(configure: stub =>
        {
            stub.Tenant = Tenant(2, 2);
            stub.Overrides[$"POST /api/channels/{OrganizationStub.Retired}/activate"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, Problem(409, message));
        });
        using var session = await harness.SignInAsync();
        var confirm = await session.GetAsync($"/organizacion/canales/{OrganizationStub.Retired}/activar");

        var response = await session.SubmitAsync(FormModel.From(confirm, confirm.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));

        response.ShouldBe(HttpStatusCode.Conflict);
        var alert = response.Document.QuerySelector("main .alert--danger")!.Plain();
        Assert.Contains(message, alert);
        Assert.Contains("desactiva una de las que tienes o pide al equipo de Conversia ampliar tu cupo", alert);
        Assert.Empty(response.Select("[data-testid=error-code]"));
        Assert.Equal($"/api/channels/{OrganizationStub.Retired}/activate", Assert.Single(harness.Posts).Path);
    }

    [Fact]
    public async Task A_conflict_without_a_message_still_explains_the_quota()
    {
        using var harness = OrganizationHarness.Create(configure: stub =>
            stub.Overrides[$"POST /api/channels/{OrganizationStub.Retired}/activate"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, null));
        using var session = await harness.SignInAsync();
        var confirm = await session.GetAsync($"/organizacion/canales/{OrganizationStub.Retired}/activar");

        var response = await session.SubmitAsync(FormModel.From(confirm, confirm.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));

        var alert = response.Document.QuerySelector("main .alert--danger")!.Plain();
        Assert.Contains("tu cupo de líneas activas está completo", alert);
        Assert.Contains("pide al equipo de Conversia ampliar tu cupo", alert);
    }

    [Fact]
    public async Task Activating_a_line_of_a_company_that_stopped_operating_goes_to_the_pending_page()
    {
        using var harness = OrganizationHarness.Create(configure: stub =>
            stub.Overrides[$"POST /api/channels/{OrganizationStub.Retired}/activate"] = _ =>
                FakeBackend.Raw(HttpStatusCode.Forbidden, """{"status":403,"title":"Empresa no activa","detail":"Tu empresa está suspendida.","code":"tenant_not_active","tenantStatus":"Suspended"}"""));
        using var session = await harness.SignInAsync();
        var confirm = await session.GetAsync($"/organizacion/canales/{OrganizationStub.Retired}/activar");

        var response = await session.SubmitAsync(FormModel.From(confirm, confirm.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));

        response.ShouldRedirectTo("/cuenta-pendiente");
    }

    [Fact]
    public async Task The_organization_page_of_a_pending_company_goes_to_the_pending_page_even_if_only_one_call_says_so()
    {
        using var harness = OrganizationHarness.Create(configure: stub =>
            stub.Overrides["GET /api/channels"] = _ =>
                FakeBackend.Raw(HttpStatusCode.Forbidden, """{"status":403,"title":"Empresa no activa","detail":"Tu empresa está pendiente de activación.","code":"tenant_not_active","tenantStatus":"Pending"}"""));

        var page = await harness.OpenAsync("/organizacion");

        page.ShouldRedirectTo("/cuenta-pendiente");
    }

    // ---- the whole flow, with the real quota rules ----

    [Collection(nameof(WebIntegrationCollection))]
    public class WithTheRealApi(TestApiFactory api)
    {
        [Fact]
        public async Task A_company_with_a_quota_of_two_can_switch_on_two_of_three_lines_and_is_told_why_not_the_third()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería", pending: true);
            await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active");
            await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 2);
            var lines = await ApiSeed.CreateLinesAsync(api, owner, "+57 300 111 0001", "+57 300 111 0002", "+57 300 111 0003");
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAsync(host, owner.Email);

            var list = (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Líneas activas 0 de 2", list.Document.QuerySelector("[data-testid=line-quota] .quota__title")!.Plain().Trim());
            Assert.Equal(["Inactiva", "Inactiva", "Inactiva"], list.Select("[data-testid=channel-state]").Select(c => c.Plain().Trim()));

            foreach (var id in lines.Take(2))
            {
                var confirm = await session.GetAsync($"/organizacion/canales/{id}/activar");
                var done = await session.SubmitAsync(FormModel.From(confirm, confirm.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));
                done.ShouldRedirectTo("/organizacion?notice=channel-on");
            }

            var third = await session.GetAsync($"/organizacion/canales/{lines[2]}/activar");
            var refused = await session.SubmitAsync(FormModel.From(third, third.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));

            refused.ShouldBe(HttpStatusCode.Conflict);
            Assert.Contains("Tu cupo de líneas activas está completo (2 de 2)", refused.Document.QuerySelector("main .alert--danger")!.Plain());
            var after = (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Líneas activas 2 de 2", after.Document.QuerySelector("[data-testid=line-quota] .quota__title")!.Plain().Trim());
            Assert.Equal("Cupo completo", after.Document.QuerySelector("[data-testid=quota-chip]")!.Plain().Trim());
            Assert.Equal(["Activa", "Activa", "Inactiva"], after.Select("[data-testid=channel-state]").Select(c => c.Plain().Trim()));

            // Switching one off frees its place for the third.
            var off = await session.GetAsync($"/organizacion/canales/{lines[0]}/desactivar");
            (await session.SubmitAsync(FormModel.From(off, off.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")))).ShouldRedirectTo("/organizacion?notice=channel-off");
            var retry = await session.GetAsync($"/organizacion/canales/{lines[2]}/activar");
            (await session.SubmitAsync(FormModel.From(retry, retry.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")))).ShouldRedirectTo("/organizacion?notice=channel-on");
        }

        [Fact]
        public async Task A_company_with_no_quota_yet_sees_it_on_the_page_and_cannot_switch_a_line_on()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería", pending: true);
            await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active");
            var lines = await ApiSeed.CreateLinesAsync(api, owner, "+57 300 222 0001");
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAsync(host, owner.Email);

            var list = (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Sin cupo asignado", list.Document.QuerySelector("[data-testid=quota-chip]")!.Plain().Trim());

            var confirm = await session.GetAsync($"/organizacion/canales/{lines[0]}/activar");
            var refused = await session.SubmitAsync(FormModel.From(confirm, confirm.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form")));

            refused.ShouldBe(HttpStatusCode.Conflict);
            Assert.Contains("(0 de 0)", refused.Document.QuerySelector("main .alert--danger")!.Plain());
        }
    }
}
