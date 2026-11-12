extern alias ApiHost;

using System.Net;
using AngleSharp.Html.Dom;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// The platform pages of the administration (docs/PLATFORM_ADMIN.md): the monitoring overview, the company's limits, AI, licences and
/// monitoring sections, the AI switch, and the licence inventory. Counts and business facts only. The states a real API cannot be put in on
/// demand use the stand-in; the main flows also run against the real API.
/// </summary>
public class AdminPlatformPagesTests
{
    private static string Detail(Guid id) => $"/admin/empresas/{id}";

    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    // ---- monitoring overview ----

    [Fact]
    public async Task The_overview_shows_the_flags_the_backlog_the_ai_and_the_silent_companies()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin/monitoreo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Monitoreo", page.Document.QuerySelector("main h1")!.Plain().Trim());
        var flags = page.Select("[data-testid=flag]").ToList();
        Assert.Equal(["webhook_backlog", "health"], flags.Select(f => f.GetAttribute("data-code")));
        Assert.Contains("612", flags[0].Plain());
        Assert.Contains("Crítica", flags[1].Plain());
        Assert.Equal("612", page.Document.QuerySelector("[data-kpi=webhook-backlog]")!.GetAttribute("data-value"));
        Assert.Contains("15 min", page.Document.QuerySelector("[data-kpi=webhook-backlog]")!.Plain());
        Assert.Contains("Abierto", page.Document.QuerySelector("[data-testid=ai-breaker]")!.Plain());
        Assert.Contains("3,50 USD de 20,00 USD", page.Document.QuerySelector("[data-testid=ai-deployment-usd]")!.Plain());
        Assert.Equal("2", page.Document.QuerySelector("[data-testid=ai-lost-usage]")!.GetAttribute("data-value"));
        Assert.Equal(["sent", "provider_limit_reached"], page.Select("[data-testid=outcome-row]").Select(r => r.GetAttribute("data-outcome")));
        var silent = Assert.Single(page.Select("[data-testid=silent-card] [data-testid=company-item]"));
        Assert.Equal(Detail(AdminStub.Aurora), silent.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Nunca ha recibido entregas", silent.Plain());
        Assert.All(harness.Backend.DataRequests, request => Assert.StartsWith("/api/admin/", request.Path));
    }

    [Fact]
    public async Task The_navigation_of_the_administration_offers_monitoring_and_licences()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        var links = page.Select("nav[aria-label='Navegación de administración'] a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal(["/admin", "/admin/monitoreo", "/admin/licencias", "/admin/facturacion", "/admin/auditoria"], links);
    }

    // ---- the company page ----

    [Fact]
    public async Task The_company_page_shows_each_limit_with_its_origin_the_ai_its_licences_and_its_counts()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var quota = page.Document.QuerySelector("[data-testid=limit-ai-quota]")!;
        Assert.Equal("true", quota.GetAttribute("data-default"));
        Assert.Contains("500 conversaciones (Por defecto)", quota.Plain());
        Assert.Contains("10,00 USD", quota.Plain());
        Assert.Contains("45 (Propio de la empresa)", page.Document.QuerySelector("[data-testid=limit-send-rate]")!.Plain());
        Assert.Equal(string.Empty, page.Document.QuerySelector("#ai-quota")!.GetAttribute("value") ?? string.Empty);
        Assert.Equal("45", page.Document.QuerySelector("#send-limit")!.GetAttribute("value"));

        Assert.Equal("false", page.Document.QuerySelector("[data-testid=company-ai]")!.GetAttribute("data-enabled"));
        Assert.Equal($"{Detail(AdminStub.Aurora)}/ia/activar", page.Document.QuerySelector("[data-testid=ai-actions] a")!.GetAttribute("href"));
        Assert.Contains("3 conversaciones", page.Document.QuerySelector("[data-testid=ai-sandbox-usage]")!.Plain());

        var license = Assert.Single(page.Select("[data-testid=license-item]"));
        Assert.Contains("•••• 0001", license.Plain());
        Assert.Equal($"/admin/licencias/{AdminStub.AuroraLicense}", license.QuerySelector("a")!.GetAttribute("href"));

        var days = page.Select("[data-testid=day-row]").ToList();
        Assert.Equal(["false", "true"], days.Select(d => d.GetAttribute("data-sandbox")));
        Assert.Contains("Reales", days[0].Plain());
        Assert.Contains("Sandbox", days[1].Plain());
        Assert.Contains("no se muestran a la administración", page.Document.QuerySelector("[data-testid=facts-card]")!.Plain());
    }

    [Fact]
    public async Task Without_monitoring_the_rest_of_the_company_page_still_works()
    {
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"GET /api/admin/tenants/{AdminStub.Aurora}/monitoring"] = _ => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, null));

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.NotNull(page.Document.QuerySelector("[data-testid=monitoring-unavailable]"));
        Assert.Equal("Activa", page.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
    }

    [Theory]
    [InlineData("ai-quota-form", "MonthlyConversations", "1200", "monthlyConversations", "cupo-ia", "ai-quota")]
    [InlineData("send-limit-form", "PerMinute", " 30 ", "perMinute", "limite-envios", "send-rate-limit")]
    public async Task Saving_a_limit_sends_the_number_and_confirms(string form, string field, string value, string json, string notice, string endpoint)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, form)).Set(field, value).Set("Reason", " Plan ampliado "));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso={notice}");
        var write = Assert.Single(harness.Writes);
        Assert.Equal($"/api/admin/tenants/{AdminStub.Aurora}/{endpoint}", write.Path);
        using var body = write.BodyJson();
        Assert.Equal(int.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture), body.RootElement.GetProperty(json).GetInt32());
        Assert.Equal("Plan ampliado", body.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("ai-quota-form", "MonthlyConversations", "800")]
    [InlineData("send-limit-form", "PerMinute", "30")]
    public async Task A_limit_without_a_reason_is_refused_on_the_page_and_nothing_is_sent(string form, string field, string value)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, form)).Set(field, value).Set("Reason", "  "));

        Assert.Contains("motivo", response.Document.QuerySelector($"[data-testid={form}] .field__error")!.Plain(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task An_empty_limit_resets_to_the_default_and_text_is_refused_on_the_page()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        await session.SubmitAsync(FormModel.From(page, Form(page, "send-limit-form")).Set("PerMinute", "").Set("Reason", "Vuelve al valor por defecto"));
        using (var body = Assert.Single(harness.Writes).BodyJson())
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, body.RootElement.GetProperty("perMinute").ValueKind);
        }

        var refused = await session.SubmitAsync(FormModel.From(page, Form(page, "ai-quota-form")).Set("MonthlyConversations", "muchas").Set("Reason", "x"));
        Assert.Contains("cupo mensual de conversaciones analizadas con IA", refused.Document.QuerySelector(".field__error")!.Plain());
        Assert.Single(harness.Writes);
    }

    // ---- the AI switch ----

    [Fact]
    public async Task Switching_the_ai_on_asks_for_the_reason_and_the_terms_and_sends_both()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/ia/activar")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Activar la IA", confirm.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.NotNull(confirm.Document.QuerySelector("#terms"));

        var noTerms = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "ai-action-form")).Set("reason", "Contrato firmado"));
        noTerms.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Empty(harness.Writes);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "ai-action-form")).Set("reason", " Contrato firmado ").Set("TermsVersion", "ia-2026-11"));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=ia-activa");
        var write = Assert.Single(harness.Writes);
        Assert.Equal($"/api/admin/tenants/{AdminStub.Aurora}/ai-enabled", write.Path);
        using var body = write.BodyJson();
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(("Contrato firmado", "ia-2026-11"), (body.RootElement.GetProperty("reason").GetString(), body.RootElement.GetProperty("termsVersion").GetString()));
        var landed = await session.FollowAsync(response);
        Assert.Contains("IA activada", landed.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }

    [Fact]
    public async Task Switching_the_ai_off_when_it_is_off_does_not_apply()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync($"{Detail(AdminStub.Aurora)}/ia/desactivar")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("ya está desactivada", page.Document.QuerySelector("[data-testid=not-allowed]")!.Plain());
        Assert.Empty(page.Select("[data-testid=ai-action-form] button[type=submit]"));
        Assert.Null(page.Document.QuerySelector("#terms"));
    }

    // ---- the licence inventory ----

    [Fact]
    public async Task The_inventory_lists_the_licences_masked_with_their_company()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin/licencias")).ShouldBe(HttpStatusCode.OK);

        var rows = page.Select("[data-testid=license-row]").ToList();
        Assert.Equal(["•••• 0001", "•••• 9876"], rows.Select(r => r.QuerySelector("th")!.Plain().Trim()));
        Assert.Equal(["Asignada", "Disponible"], rows.Select(r => r.QuerySelector(".chip")!.Plain().Trim()));
        Assert.Equal(Detail(AdminStub.Aurora), rows[0].QuerySelector("td[data-label=Empresa] a")!.GetAttribute("href"));
        Assert.Equal("/admin/licencias/nueva", page.Document.QuerySelector("[data-testid=new-license]")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Creating_a_licence_sends_the_number_and_the_cost_and_lands_on_it()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync("/admin/licencias/nueva")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "license-form"))
            .Set("PhoneNumber", "+57 310 222 4455").Set("MonthlyCostUsd", "12,50"));

        Assert.True(response.IsRedirect);
        using var body = Assert.Single(harness.Writes).BodyJson();
        Assert.Equal(("+57 310 222 4455", 12.50m), (body.RootElement.GetProperty("phoneNumber").GetString(), body.RootElement.GetProperty("monthlyCostUsd").GetDecimal()));
        var landed = await session.FollowAsync(response);
        Assert.Equal("•••• 4455", landed.Document.QuerySelector("[data-testid=license-number]")!.Plain().Trim());
        Assert.Contains("Licencia creada", landed.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.DoesNotContain("310 222", landed.Text);
    }

    [Fact]
    public async Task A_bad_number_or_cost_is_refused_on_the_page_and_nothing_is_sent()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync("/admin/licencias/nueva")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "license-form")).Set("PhoneNumber", "12").Set("MonthlyCostUsd", "barato"));

        var errors = response.Select(".field__error").Select(e => e.Plain()).ToList();
        Assert.Contains(errors, e => e.Contains("número de teléfono"));
        Assert.Contains(errors, e => e.Contains("costo mensual"));
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task An_available_licence_offers_assigning_and_retiring_and_an_assigned_one_releasing()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();

        var free = (await session.GetAsync($"/admin/licencias/{AdminStub.FreeLicense}")).ShouldBe(HttpStatusCode.OK);
        Assert.NotEmpty(free.Select("[data-testid=assign-form] button[type=submit]"));
        Assert.NotEmpty(free.Select("[data-testid=retire-form] button[type=submit]"));
        Assert.Empty(free.Select("[data-testid=release-form] button[type=submit]"));
        Assert.Contains("Lote <b>noviembre</b>", free.Document.QuerySelector("[data-testid=license-card]")!.TextContent); // encoded, never markup

        var assigned = (await session.GetAsync($"/admin/licencias/{AdminStub.AuroraLicense}")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty(assigned.Select("[data-testid=assign-form] button[type=submit]"));
        Assert.NotEmpty(assigned.Select("[data-testid=release-form] button[type=submit]"));

        var released = await session.SubmitAsync(FormModel.From(assigned, Form(assigned, "release-form")));
        released.ShouldRedirectTo($"/admin/licencias/{AdminStub.AuroraLicense}?aviso=liberada");
        Assert.Equal($"/api/admin/licenses/{AdminStub.AuroraLicense}/release", Assert.Single(harness.Writes).Path);
    }

    [Fact]
    public async Task Assigning_sends_the_chosen_company()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync($"/admin/licencias/{AdminStub.FreeLicense}")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "assign-form")).Set("TenantId", AdminStub.Aurora.ToString()));

        response.ShouldRedirectTo($"/admin/licencias/{AdminStub.FreeLicense}?aviso=asignada");
        using var body = Assert.Single(harness.Writes).BodyJson();
        Assert.Equal(AdminStub.Aurora, body.RootElement.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task A_conflict_of_the_api_is_shown_in_its_words()
    {
        const string message = "La licencia no está disponible: está asignada a una empresa o retirada.";
        using var harness = AdminHarness.Create(stub => stub.Overrides[$"POST /api/admin/licenses/{AdminStub.FreeLicense}/retire"] =
            _ => FakeBackend.Raw(HttpStatusCode.Conflict, $$"""{"status":409,"title":"x","detail":"{{message}}","code":"license_not_available"}"""));
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync($"/admin/licencias/{AdminStub.FreeLicense}")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "retire-form")));

        response.ShouldBe(HttpStatusCode.Conflict);
        Assert.Equal(message, response.Document.QuerySelector("[data-testid=license-conflict]")!.Plain().Trim());
    }

    [Fact]
    public async Task An_unknown_licence_is_the_not_found_page_of_the_licences()
    {
        using var harness = AdminHarness.Create();

        var page = await harness.OpenAsync($"/admin/licencias/{Guid.NewGuid()}");

        Assert.Equal(404, page.Status);
        Assert.Equal("Licencia no encontrada", page.Document.QuerySelector("[data-page=not-found] h1")!.Plain().Trim());
        Assert.NotNull(page.Document.QuerySelector("[data-shell=app] a[href='/admin/auditoria']"));
    }

    // ---- the real API ----

    [Collection(nameof(WebIntegrationCollection))]
    public class WithTheRealApi(TestApiFactory api)
    {
        [Fact]
        public async Task A_licence_created_and_assigned_through_the_pages_appears_on_the_company_and_in_the_trail()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "licencias");
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);
            var digits = "31" + Random.Shared.NextInt64(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var form = (await session.GetAsync("/admin/licencias/nueva")).ShouldBe(HttpStatusCode.OK);
            var created = await session.SubmitAsync(FormModel.From(form, Form(form, "license-form")).Set("PhoneNumber", "+57 " + digits).Set("MonthlyCostUsd", "9.90"));
            var detail = await session.FollowAsync(created);
            Assert.Equal("•••• " + digits[^4..], detail.Document.QuerySelector("[data-testid=license-number]")!.Plain().Trim());

            var assigned = await session.SubmitAsync(FormModel.From(detail, Form(detail, "assign-form")).Set("TenantId", owner.TenantId.ToString()));
            var afterAssign = await session.FollowAsync(assigned);
            Assert.Equal("Assigned", afterAssign.Document.QuerySelector("[data-testid=license-status]")!.GetAttribute("data-status"));

            var company = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Contains("•••• " + digits[^4..], Assert.Single(company.Select("[data-testid=license-item]")).Plain());
            Assert.Contains(company.Select("[data-testid=audit-entry]").Select(e => e.Plain()), t => t.Contains("Asignó una licencia de línea") && t.Contains("•••• " + digits[^4..]));
            Assert.DoesNotContain(digits, company.Text);
            Assert.NotNull(company.Document.QuerySelector("[data-testid=monitoring-card] .kv"));
        }

        [Fact]
        public async Task Switching_the_ai_on_and_setting_its_quota_through_the_pages_is_saved_and_audited()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "ia");
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var confirm = (await session.GetAsync($"{Detail(owner.TenantId)}/ia/activar")).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "ai-action-form")).Set("reason", "Contrato firmado").Set("TermsVersion", "ia-v1")))
                .ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=ia-activa");
            var page = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            (await session.SubmitAsync(FormModel.From(page, Form(page, "ai-quota-form")).Set("MonthlyConversations", "800").Set("Reason", "Plan ampliado")))
                .ShouldRedirectTo($"{Detail(owner.TenantId)}?aviso=cupo-ia");

            var done = (await session.GetAsync(Detail(owner.TenantId))).ShouldBe(HttpStatusCode.OK);
            Assert.Equal("true", done.Document.QuerySelector("[data-testid=company-ai]")!.GetAttribute("data-enabled"));
            Assert.Equal("ia-v1", done.Document.QuerySelector("[data-testid=ai-terms]")!.Plain().Trim());
            Assert.Contains("800 conversaciones (Propio de la empresa)", done.Document.QuerySelector("[data-testid=limit-ai-quota]")!.Plain());
            var trail = done.Select("[data-testid=audit-entry]").Select(e => e.Plain()).ToList();
            Assert.Contains(trail, t => t.Contains("Activó la IA") && t.Contains("Motivo: Contrato firmado") && t.Contains("Términos: ia-v1"));
            Assert.Contains(trail, t => t.Contains("Cambió el cupo mensual de IA: por defecto → 800") && t.Contains("Motivo: Plan ampliado"));
        }

        [Fact]
        public async Task The_overview_works_against_the_real_api()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var page = (await session.GetAsync("/admin/monitoreo")).ShouldBe(HttpStatusCode.OK);

            Assert.NotNull(page.Document.QuerySelector("[data-kpi=health]"));
            Assert.NotNull(page.Document.QuerySelector("[data-testid=deliveries-card]"));
        }
    }
}
