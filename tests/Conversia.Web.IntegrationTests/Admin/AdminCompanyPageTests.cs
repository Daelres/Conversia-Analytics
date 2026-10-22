extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Dom;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Organization;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// One company as the administrator sees it: its status and the moves that make sense, the line quota, its lines and the changes made to it.
/// Business facts only. The states a real API cannot be put in on demand use a stand-in API; the flows with the real rules use the real one.
/// </summary>
public class AdminCompanyPageTests
{
    private static string Detail(Guid id) => $"/admin/empresas/{id}";

    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    private static string Problem(int status, string detail) => $$"""{"status":{{status}},"title":"x","detail":"{{detail}}"}""";

    // ---- the page, with a stand-in API ----

    [Fact]
    public async Task The_page_shows_the_status_the_quota_the_lines_and_the_business_facts_of_the_company()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Depilación Aurora", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Activa", page.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
        Assert.Equal("Acuerdo firmado", page.Document.QuerySelector("[data-testid=status-reason]")!.Plain().Trim());
        Assert.Equal("2 sep 2026, 10:00", page.Document.QuerySelector("[data-testid=status-changed-at]")!.Plain().Trim());
        Assert.Equal("Líneas activas 1 de 2", page.Document.QuerySelector("[data-testid=quota-card] .quota__title")!.Plain().Trim());
        Assert.Equal("2", page.Document.QuerySelector("#quota")!.GetAttribute("value"));
        Assert.Equal("aurora@stub.test", page.Document.QuerySelector("[data-testid=owner-email]")!.Plain().Trim());
        Assert.Equal("20 oct 2026, 10:05", page.Document.QuerySelector("[data-testid=last-activity]")!.Plain().Trim());
        var lines = page.Select("[data-testid=line-row]").ToList();
        Assert.Equal(["+57 300 000 0001", "+57 300 000 0002"], lines.Select(l => l.QuerySelector("th")!.Plain().Trim()));
        Assert.Equal(["Centro", "—"], lines.Select(l => l.QuerySelector("td[data-label=Sucursal]")!.Plain().Trim()));
        Assert.Equal(["Activa", "Inactiva"], lines.Select(l => l.QuerySelector(".chip")!.Plain().Trim()));
    }

    [Fact]
    public async Task The_page_never_offers_conversations_messages_or_metrics_of_the_company()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();

        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var links = page.Select("a[href]").Select(a => a.GetAttribute("href")!).ToList();
        Assert.DoesNotContain(links, href => href is "/panel" or "/conversaciones" or "/organizacion" or "/users" || href.StartsWith("/conversaciones/", StringComparison.Ordinal));
        Assert.Contains("no se muestran a la administración", page.Document.QuerySelector("[data-testid=facts-card]")!.Plain());
        Assert.All(harness.Backend.DataRequests, request => Assert.StartsWith("/api/admin/", request.Path)); // nothing else is ever asked of the API
    }

    [Theory]
    [InlineData("Norte", new[] { "activar", "suspender" })]
    [InlineData("Aurora", new[] { "suspender" })]
    [InlineData("Sur", new[] { "reactivar" })]
    public async Task Only_the_moves_that_make_sense_for_the_status_are_offered(string company, string[] actions)
    {
        var id = company switch { "Norte" => AdminStub.Norte, "Aurora" => AdminStub.Aurora, _ => AdminStub.Sur };
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync(Detail(id))).ShouldBe(HttpStatusCode.OK);

        var offered = page.Select("[data-testid=status-actions] a").ToList();
        Assert.Equal(actions, offered.Select(a => a.GetAttribute("data-action")));
        Assert.Equal(actions.Select(a => $"{Detail(id)}/estado/{a}"), offered.Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public async Task A_line_that_is_off_has_no_deactivate_button_and_one_that_is_on_has_it_with_the_number_in_its_name()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var rows = page.Select("[data-testid=line-row]").ToList();
        var on = Assert.Single(rows[0].QuerySelectorAll("a"));
        Assert.Equal($"{Detail(AdminStub.Aurora)}/lineas/{AdminStub.AuroraLine}/desactivar", on.GetAttribute("href"));
        Assert.Equal($"ld-{AdminStub.AuroraLine} ln-{AdminStub.AuroraLine}", on.GetAttribute("aria-labelledby"));
        Assert.Empty(rows[1].QuerySelectorAll("a"));
    }

    [Theory]
    [InlineData("a0a0a0a0-0000-4000-8000-000000000001")]
    public async Task An_unknown_company_is_the_not_found_page_of_the_administration(string id)
    {
        using var harness = AdminHarness.Create();

        var page = await harness.OpenAsync(Detail(Guid.Parse(id)));

        Assert.Equal(404, page.Status);
        Assert.Equal("Empresa no encontrada", page.Document.QuerySelector("[data-page=not-found] h1")!.Plain().Trim());
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/admin");
        Assert.NotNull(page.Document.QuerySelector("[data-shell=app] a[href='/admin/auditoria']")); // still the administrator's shell
    }

    [Fact]
    public async Task A_route_that_is_not_a_guid_is_a_plain_not_found()
    {
        using var harness = AdminHarness.Create();

        var page = await harness.OpenAsync("/admin/empresas/no-es-un-id");

        Assert.Equal(404, page.Status);
    }

    // ---- changing the status ----

    [Fact]
    public async Task Each_move_asks_first_and_changes_nothing_until_confirmed()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync($"{Detail(AdminStub.Norte)}/estado/activar")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Activar empresa", page.Document.QuerySelector("main h1")!.Plain().Trim());
        var confirm = page.Document.QuerySelector("[data-testid=status-confirm]")!;
        Assert.Contains("¿Activar esta empresa?", confirm.Plain());
        Assert.Contains("Pendiente", confirm.Plain());
        Assert.Contains("podrá operar", confirm.QuerySelector("[data-testid=confirm-effect]")!.Plain());
        Assert.Equal("500", page.Document.QuerySelector("#reason")!.GetAttribute("maxlength"));
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == Detail(AdminStub.Norte) && a.Plain().Contains("Cancelar"));
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task Confirming_sends_the_status_and_the_reason_and_lands_on_the_company_with_a_confirmation()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Norte)}/estado/activar")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("reason", "  Acuerdo firmado  "));

        response.ShouldRedirectTo($"{Detail(AdminStub.Norte)}?aviso=estado");
        var write = Assert.Single(harness.Writes);
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.Equal($"/api/admin/tenants/{AdminStub.Norte}/status", write.Path);
        using var body = write.BodyJson();
        Assert.Equal("Active", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Acuerdo firmado", body.RootElement.GetProperty("reason").GetString());
        var landed = await session.FollowAsync(response);
        Assert.Equal("Estado actualizado.", landed.Document.QuerySelector("[data-testid=notice-success]")!.Plain().Trim());
        Assert.Equal("Activa", landed.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
    }

    [Theory]
    [InlineData("suspender", "Suspended")]
    [InlineData("activar", "Active")]
    public async Task The_move_is_the_one_the_address_names_and_an_empty_reason_is_not_sent(string action, string status)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Norte)}/estado/{action}")).ShouldBe(HttpStatusCode.OK);

        await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("reason", "   "));

        using var body = Assert.Single(harness.Writes).BodyJson();
        Assert.Equal(status, body.RootElement.GetProperty("status").GetString());
        Assert.Null(body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_reason_over_five_hundred_characters_is_refused_on_the_page_and_nothing_is_sent()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Norte)}/estado/suspender")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("reason", new string('x', 501)));

        Assert.Contains("El motivo no puede superar los 500 caracteres.", response.Document.QuerySelector(".field__error")!.Plain());
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task A_move_the_company_no_longer_allows_shows_the_apis_message_and_stays_on_the_confirmation()
    {
        const string message = "No se puede pasar la empresa de «activa» a «activa».";
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"PUT /api/admin/tenants/{AdminStub.Norte}/status"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, Problem(409, message)));
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Norte)}/estado/activar")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")));

        response.ShouldBe(HttpStatusCode.Conflict);
        Assert.Equal(message, response.Document.QuerySelector("main .alert--danger")!.Plain().Trim());
        Assert.Empty(response.Select("[data-testid=error-code]"));
    }

    [Fact]
    public async Task A_move_that_does_not_apply_to_the_current_status_says_so_and_shows_no_button()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync($"{Detail(AdminStub.Aurora)}/estado/reactivar")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Esta acción no aplica ahora", page.Document.QuerySelector("[data-testid=not-allowed]")!.Plain());
        Assert.Contains("activa", page.Document.QuerySelector("[data-testid=not-allowed]")!.Plain());
        Assert.True(PageContract.IsHidden(page.Document.QuerySelector("[data-testid=status-confirm]")!));
        Assert.Empty(page.Select("[data-testid=status-action-form] button[type=submit]"));
    }

    [Theory]
    [InlineData("borrar")]
    [InlineData("ACTIVAR")]
    [InlineData("")]
    public async Task An_unknown_move_is_a_not_found(string action)
    {
        using var harness = AdminHarness.Create();

        var page = await harness.OpenAsync($"{Detail(AdminStub.Norte)}/estado/{action}");

        Assert.Equal(404, page.Status);
    }

    // ---- the quota ----

    [Fact]
    public async Task Saving_the_quota_sends_the_number_and_confirms()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", " 5 "));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=cupo");
        using var body = Assert.Single(harness.Writes).BodyJson();
        Assert.Equal(5, body.RootElement.GetProperty("maxActiveLines").GetInt32());
        var landed = await session.FollowAsync(response);
        Assert.Equal("Cupo guardado.", landed.Document.QuerySelector("[data-testid=notice-success]")!.Plain().Trim());
        Assert.Equal("Líneas activas 1 de 5", landed.Document.QuerySelector("[data-testid=quota-card] .quota__title")!.Plain().Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("2.5")]
    [InlineData("99999999999999999999")]
    public async Task A_quota_that_is_not_a_whole_number_from_zero_to_one_thousand_is_refused_before_the_api(string value)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", value));

        Assert.Contains("entre 0 y 1000", response.Document.QuerySelector("[data-testid=quota-card] .field__error")!.Plain());
        Assert.Empty(harness.Writes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1000")]
    public async Task The_limits_of_the_quota_are_accepted(string value)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", value));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=cupo");
    }

    [Fact]
    public async Task A_quota_below_the_active_lines_shows_the_apis_message_and_keeps_the_typed_number()
    {
        const string message = "No se puede bajar el cupo por debajo de las líneas activas: la empresa tiene 1 activa(s). Desactiva líneas primero.";
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"PUT /api/admin/tenants/{AdminStub.Aurora}/line-quota"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, Problem(409, message)));
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", "0"));

        response.ShouldBe(HttpStatusCode.Conflict);
        Assert.Equal(message, response.Document.QuerySelector("[data-testid=quota-error]")!.Plain().Trim());
        Assert.Equal("0", response.Document.QuerySelector("#quota")!.GetAttribute("value"));
        Assert.Equal("Depilación Aurora", response.Document.QuerySelector("main h1")!.Plain().Trim()); // the page is whole, not an error screen
    }

    [Fact]
    public async Task The_apis_own_validation_of_the_quota_is_shown_next_to_the_field()
    {
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"PUT /api/admin/tenants/{AdminStub.Aurora}/line-quota"] = _ =>
            FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"MaxActiveLines":["El cupo no es válido."]}}"""));
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", "3"));

        Assert.Equal(400, response.Status);
        Assert.Contains("El cupo no es válido.", response.Document.QuerySelector("[data-testid=quota-card] .field__error")!.Plain());
    }

    // ---- switching a line off ----

    [Fact]
    public async Task Switching_a_line_off_asks_first_then_posts_and_confirms()
    {
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"POST /api/admin/tenants/{AdminStub.Aurora}/channels/{AdminStub.AuroraLine}/deactivate"] =
            _ => FakeBackend.Answer(HttpStatusCode.OK, new TenantLineResponse { ChannelId = AdminStub.AuroraLine, DisplayPhoneNumber = "+57 300 000 0001", IsActive = false }));
        using var session = await harness.SignInAsync();
        var url = $"{Detail(AdminStub.Aurora)}/lineas/{AdminStub.AuroraLine}/desactivar";

        var confirm = (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("+57 300 000 0001", confirm.Document.QuerySelector("[data-testid=confirm-number]")!.Plain().Trim());
        Assert.Contains("libera su lugar en el cupo", confirm.Document.QuerySelector("[data-testid=confirm-effect]")!.Plain());
        Assert.Empty(harness.Writes);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "line-action-form")));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=linea");
        Assert.Equal($"/api/admin/tenants/{AdminStub.Aurora}/channels/{AdminStub.AuroraLine}/deactivate", Assert.Single(harness.Writes).Path);
        Assert.Equal("Línea desactivada: dejó de recibir mensajes.", (await session.FollowAsync(response)).Document.QuerySelector("[data-testid=notice-success]")!.Plain().Trim());
    }

    [Fact]
    public async Task A_line_that_is_already_off_or_not_the_companys_has_nothing_to_confirm()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();

        var off = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/lineas/{AdminStub.AuroraOffLine}/desactivar")).ShouldBe(HttpStatusCode.OK);
        var foreign = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/lineas/{Guid.NewGuid()}/desactivar")).ShouldBe(HttpStatusCode.OK);

        foreach (var page in new[] { off, foreign })
        {
            Assert.True(PageContract.IsHidden(page.Document.QuerySelector("[data-testid=line-confirm]")!));
            Assert.Empty(page.Select("[data-testid=line-action-form] button[type=submit]"));
        }

        Assert.NotNull(foreign.Document.QuerySelector("[data-testid=line-missing]"));
    }

    // ---- when the administrator is refused ----

    [Fact]
    public async Task A_write_the_api_refuses_to_the_administrator_is_the_forbidden_page()
    {
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"PUT /api/admin/tenants/{AdminStub.Aurora}/line-quota"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null));
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", "3"));

        Assert.Equal(403, response.Status);
    }

    // ---- the real API ----

    [Collection(nameof(WebIntegrationCollection))]
    public class WithTheRealApi(TestApiFactory api)
    {
        [Fact]
        public async Task Approving_a_company_through_the_pages_opens_it_to_its_owner_and_leaves_the_trail()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "estética", pending: true);
            using var host = WebTestHost.Create(api);
            using var company = await WebFlows.SignedInAsync(host, owner.Email);
            (await company.GetAsync("/organizacion")).ShouldRedirectTo("/cuenta-pendiente");
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var start = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Pendiente", start.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
            Assert.Equal(["activar", "suspender"], start.Select("[data-testid=status-actions] a").Select(a => a.GetAttribute("data-action")));

            var confirm = (await session.GetAsync($"{Detail(owner.TenantId)}/estado/activar")).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("reason", "Acuerdo firmado"))).ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=estado");
            var quota = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(quota, Form(quota, "quota-form")).Set("MaxActiveLines", "2"))).ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=cupo");

            var done = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Activa", done.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
            Assert.Equal("Acuerdo firmado", done.Document.QuerySelector("[data-testid=status-reason]")!.Plain().Trim());
            Assert.Equal("Líneas activas 0 de 2", done.Document.QuerySelector("[data-testid=quota-card] .quota__title")!.Plain().Trim());
            Assert.Equal(["suspender"], done.Select("[data-testid=status-actions] a").Select(a => a.GetAttribute("data-action")));
            var trail = done.Select("[data-testid=audit-entry]").Select(e => e.Plain()).ToList();
            Assert.Contains(trail, t => t.Contains("Cambió el estado: Pendiente → Activa") && t.Contains("Motivo: Acuerdo firmado") && t.Contains(admin.Email));
            Assert.Contains(trail, t => t.Contains("Cambió el cupo de líneas: 0 → 2"));

            // The company's own session now operates.
            (await company.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
            (await company.GetAsync("/cuenta-pendiente")).ShouldRedirectTo("/panel");
        }

        [Fact]
        public async Task Suspending_stops_the_company_at_once_and_reactivating_brings_it_back()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "clínica");
            using var host = WebTestHost.Create(api);
            using var company = await WebFlows.SignedInAsync(host, owner.Email);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var suspend = (await session.GetAsync($"{Detail(owner.TenantId)}/estado/suspender")).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(suspend, Form(suspend, "status-action-form")).Set("reason", "Falta de pago"))).ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=estado");

            (await company.GetAsync("/organizacion")).ShouldRedirectTo("/cuenta-pendiente");
            var suspended = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Suspendida", suspended.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
            Assert.Equal(["reactivar"], suspended.Select("[data-testid=status-actions] a").Select(a => a.GetAttribute("data-action")));

            var reactivate = (await session.GetAsync($"{Detail(owner.TenantId)}/estado/reactivar")).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(reactivate, Form(reactivate, "status-action-form")))).ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=estado");
            (await company.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task A_stale_confirmation_shows_the_apis_message_about_the_invalid_move()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "estética", pending: true);
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);
            var confirm = (await session.GetAsync($"{Detail(owner.TenantId)}/estado/activar")).ShouldBe(HttpStatusCode.OK);

            await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active"); // someone else got there first
            var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")));

            response.ShouldBe(HttpStatusCode.Conflict);
            Assert.Contains("No se puede pasar la empresa de «activa» a «activa».", response.Document.QuerySelector("main .alert--danger")!.Plain());
        }

        [Fact]
        public async Task The_quota_cannot_go_below_the_active_lines_and_the_message_says_how_many()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería");
            await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 2);
            var lines = await ApiSeed.CreateLinesAsync(api, owner, "+57 300 333 0001", "+57 300 333 0002");
            foreach (var id in lines)
            {
                await ApiSeed.ActivateLineAsync(api, owner, id);
            }

            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);
            var page = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Líneas activas 2 de 2", page.Document.QuerySelector("[data-testid=quota-card] .quota__title")!.Plain().Trim());

            var refused = await session.SubmitAsync(FormModel.From(page, Form(page, "quota-form")).Set("MaxActiveLines", "1"));

            refused.ShouldBe(HttpStatusCode.Conflict);
            Assert.Contains("la empresa tiene 2 activa(s)", refused.Document.QuerySelector("[data-testid=quota-error]")!.Plain());
            var after = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("2", after.Document.QuerySelector("#quota")!.GetAttribute("value")); // unchanged
        }

        [Fact]
        public async Task Switching_a_line_off_from_the_pages_turns_it_off_for_the_company_and_frees_the_quota()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería");
            await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 1);
            var lines = await ApiSeed.CreateLinesAsync(api, owner, "+57 300 444 0001");
            await ApiSeed.ActivateLineAsync(api, owner, lines[0]);
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var detail = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            var row = Assert.Single(detail.Select("[data-testid=line-row]"));
            Assert.Equal("true", row.GetAttribute("data-active"));
            var confirm = (await session.GetAsync(row.QuerySelector("a")!.GetAttribute("href")!)).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "line-action-form")))).ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=linea");

            var after = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("Líneas activas 0 de 1", after.Document.QuerySelector("[data-testid=quota-card] .quota__title")!.Plain().Trim());
            Assert.Equal("Inactiva", after.Document.QuerySelector("[data-testid=line-row] .chip")!.Plain().Trim());
            using var ownerClient = ApiSeed.AuthorizedClient(api, owner.Token);
            var channel = (await ownerClient.GetFromJsonAsync<ChannelResponse>($"/api/channels/{lines[0]}"))!;
            Assert.False(channel.IsActive);
            Assert.Contains(after.Select("[data-testid=audit-entry]"), e => e.Plain().Contains("Desactivó una línea") && e.Plain().Contains("+57 300 444 0001"));
        }

        [Fact]
        public async Task A_line_of_another_company_cannot_be_switched_off_through_this_ones_address()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var mine = await ApiSeed.RegisterOwnerAsync(api, "peluquería");
            var other = await ApiSeed.RegisterOwnerAsync(api, "droguería");
            await ApiSeed.SetQuotaAsync(api, admin, other.TenantId, 1);
            var foreign = (await ApiSeed.CreateLinesAsync(api, other, "+57 300 555 0001"))[0];
            await ApiSeed.ActivateLineAsync(api, other, foreign);
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var page = await session.GetAsync($"{Detail(mine.TenantId)}/lineas/{foreign}/desactivar");

            Assert.Empty(page.Select("[data-testid=line-action-form] button[type=submit]"));
            using var ownerClient = ApiSeed.AuthorizedClient(api, other.Token);
            Assert.True((await ownerClient.GetFromJsonAsync<ChannelResponse>($"/api/channels/{foreign}"))!.IsActive);
        }

        [Fact]
        public async Task A_company_that_does_not_exist_is_the_same_not_found_whatever_the_page()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);
            var ghost = Guid.NewGuid();

            foreach (var url in new[] { Detail(ghost), $"{Detail(ghost)}/estado/activar", $"{Detail(ghost)}/lineas/{Guid.NewGuid()}/desactivar" })
            {
                var page = await session.GetAsync(url);
                Assert.Equal(404, page.Status);
                Assert.NotNull(page.Document.QuerySelector("[data-page=not-found]"));
            }
        }
    }
}
