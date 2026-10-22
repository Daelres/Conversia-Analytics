extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>The administrator's list of companies: what each row says, the filters, the paging and the states of the data (stand-in API) and the real thing (real API).</summary>
public class AdminCompaniesPageTests
{
    // ---- the rows, with a stand-in API ----

    [Fact]
    public async Task Each_company_is_a_row_with_its_status_lines_owner_registration_and_last_activity()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Empresas", page.Document.QuerySelector("main h1")!.Plain().Trim());
        var rows = page.Select("[data-testid=company-row]").ToList();
        Assert.Equal(3, rows.Count);
        var aurora = rows.Single(r => r.GetAttribute("data-company-id") == AdminStub.Aurora.ToString());
        Assert.Equal("Depilación Aurora", aurora.QuerySelector("th a")!.Plain().Trim());
        Assert.Equal($"/admin/empresas/{AdminStub.Aurora}", aurora.QuerySelector("th a")!.GetAttribute("href"));
        Assert.Equal("Activa", aurora.QuerySelector("[data-chip=tenant-status]")!.Plain().Trim());
        Assert.Equal("1 de 2", aurora.QuerySelector("[data-testid=company-lines]")!.Plain().Trim());
        Assert.Equal("aurora@stub.test", aurora.QuerySelector("td[data-label=Propietario]")!.Plain().Trim());
        Assert.Equal("1 sep 2026", aurora.QuerySelector("td[data-label=Registro]")!.Plain().Trim());
        Assert.Equal("20 oct 2026, 10:05", aurora.QuerySelector("[data-testid=company-activity]")!.Plain().Trim()); // UTC shown in the company's zone (Bogotá)

        var norte = rows.Single(r => r.GetAttribute("data-company-id") == AdminStub.Norte.ToString());
        Assert.Equal("Pendiente", norte.QuerySelector("[data-chip=tenant-status]")!.Plain().Trim());
        Assert.Equal("0 de 0", norte.QuerySelector("[data-testid=company-lines]")!.Plain().Trim());
        Assert.Equal("Sin actividad", norte.QuerySelector("[data-testid=company-activity]")!.Plain().Trim());

        var sur = rows.Single(r => r.GetAttribute("data-company-id") == AdminStub.Sur.ToString());
        Assert.Equal("Suspendida", sur.QuerySelector("[data-chip=tenant-status]")!.Plain().Trim());
        Assert.Equal("—", sur.QuerySelector("td[data-label=Propietario]")!.Plain().Trim()); // a company without an owner is a dash, not a blank
    }

    [Fact]
    public async Task The_table_scrolls_inside_its_own_region_and_is_labelled()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        var region = page.Document.QuerySelector(".table-wrap")!;
        Assert.Equal("region", region.GetAttribute("role"));
        Assert.Equal("Empresas", region.GetAttribute("aria-label"));
        Assert.Equal("0", region.GetAttribute("tabindex")); // keyboard users can scroll it
        Assert.NotNull(region.QuerySelector("table.table--cards caption.sr-only"));
        Assert.All(region.QuerySelectorAll("thead th"), th => Assert.Equal("col", th.GetAttribute("scope")));
        Assert.All(region.QuerySelectorAll("tbody tr"), tr => Assert.Equal("row", tr.QuerySelector("th")!.GetAttribute("scope")));
    }

    [Fact]
    public async Task A_company_name_and_a_reason_are_shown_as_text_never_as_markup()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();

        var list = (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        var detail = (await session.GetAsync($"/admin/empresas/{AdminStub.Sur}")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Clínica <b>Sur</b>", list.Select("[data-testid=company-row] th a").Select(a => a.Plain().Trim()).Single(t => t.Contains("Sur")));
        Assert.DoesNotContain("<b>Sur</b>", list.Body);
        Assert.Empty(list.Select("th a b"));
        Assert.Equal("<script>alert(1)</script>", detail.Document.QuerySelector("[data-testid=status-reason]")!.Plain().Trim());
        Assert.Empty(detail.Select("main script"));
    }

    [Theory]
    [InlineData("estado=pendiente", new[] { "Estética Norte" })]
    [InlineData("estado=activa", new[] { "Depilación Aurora" })]
    [InlineData("estado=suspendida", new[] { "Clínica <b>Sur</b>" })]
    [InlineData("buscar=aurora", new[] { "Depilación Aurora" })]
    [InlineData("buscar=NORTE&estado=pendiente", new[] { "Estética Norte" })]
    [InlineData("estado=desconocido", new[] { "Depilación Aurora", "Estética Norte", "Clínica <b>Sur</b>" })]
    public async Task The_filters_narrow_the_list_and_an_unknown_value_is_ignored(string query, string[] expected)
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin?" + query)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(expected.Order(), page.Select("[data-testid=company-row] th a").Select(a => a.Plain().Trim()).Order());
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so_and_offers_to_clear_it()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin?buscar=zzz")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Ninguna empresa coincide", page.Document.QuerySelector("[data-testid=empty-state] .empty__title")!.Plain().Trim());
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/admin" && a.Plain().Contains("Borrar filtros"));
        Assert.Equal("zzz", page.Document.QuerySelector("#f-search")!.GetAttribute("value"));
    }

    [Fact]
    public async Task The_filters_are_sent_to_the_api_escaped_and_the_page_size_is_twenty()
    {
        using var harness = AdminHarness.Create();

        await harness.OpenAsync("/admin?buscar=" + Uri.EscapeDataString("Ñ&=a b") + "&estado=activa&pagina=3");

        var call = harness.Backend.DataRequests.First(r => r.Path == "/api/admin/tenants" && r.Uri.Query.Contains("search"));
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal("Ñ&=a b", query["search"]);
        Assert.Equal("Active", query["status"]);
        Assert.Equal("3", query["page"]);
        Assert.Equal("20", query["pageSize"]);
    }

    [Theory]
    [InlineData("pagina=0")]
    [InlineData("pagina=-4")]
    [InlineData("pagina=abc")]
    [InlineData("pagina=99999999999999999999")]
    public async Task A_page_number_that_is_not_one_falls_back_to_the_first_page(string query)
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin?" + query)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(3, page.Select("[data-testid=company-row]").Count());
        Assert.Contains("Página 1 de 1", page.Document.QuerySelector("[data-testid=pager]")!.Plain());
    }

    [Fact]
    public async Task Companies_waiting_for_activation_are_called_out_with_a_link_to_them()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        var banner = page.Document.QuerySelector("[data-testid=pending-banner]")!;
        Assert.Contains("1 empresa espera activación.", banner.Plain());
        Assert.Equal("/admin?estado=pendiente", banner.QuerySelector("a")!.GetAttribute("href"));
        var pending = (await harness.OpenAsync("/admin?estado=pendiente")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty(pending.Select("[data-testid=pending-banner]")); // it is the list of them already
    }

    [Fact]
    public async Task With_nothing_pending_there_is_no_banner_and_with_no_companies_there_is_a_welcome()
    {
        using var harness = AdminHarness.Create(stub => stub.Companies.Clear());

        var page = (await harness.OpenAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("[data-testid=pending-banner]"));
        Assert.Equal("Aún no hay empresas", page.Document.QuerySelector("[data-testid=empty-state] .empty__title")!.Plain().Trim());
    }

    [Fact]
    public async Task Paging_keeps_the_filters_and_the_pager_says_where_you_are()
    {
        using var harness = AdminHarness.Create(stub =>
        {
            var template = stub.Companies[0];
            for (var i = 0; i < 24; i++)
            {
                stub.Companies.Add(new() { Id = Guid.NewGuid(), Name = $"Masaje {i:00}", Status = Conversia.Domain.Enums.TenantStatus.Active, RegisteredAt = template.RegisteredAt, OwnerEmail = $"m{i}@stub.test" });
            }
        });

        var first = (await harness.OpenAsync("/admin?buscar=masaje")).ShouldBe(HttpStatusCode.OK);
        var second = (await harness.OpenAsync("/admin?buscar=masaje&pagina=2")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(20, first.Select("[data-testid=company-row]").Count());
        Assert.Equal(4, second.Select("[data-testid=company-row]").Count());
        Assert.Contains("Página 1 de 2", first.Document.QuerySelector("[data-testid=pager]")!.Plain());
        Assert.Contains("24 empresas", first.Document.QuerySelector("[data-testid=pager]")!.Plain());
        Assert.Equal("/admin?buscar=masaje&pagina=2", first.Document.QuerySelector("a[data-testid=pager-next]")!.GetAttribute("href"));
        Assert.Empty(first.Select("a[data-testid=pager-prev]"));
        Assert.Equal("/admin?buscar=masaje", second.Document.QuerySelector("a[data-testid=pager-prev]")!.GetAttribute("href"));
        Assert.Empty(second.Select("a[data-testid=pager-next]"));
    }

    // ---- failures ----

    [Theory]
    [MemberData(nameof(FailureNames))]
    public async Task A_backend_that_fails_is_said_plainly_and_shows_no_rows(string failure)
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        harness.Host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync("/admin");

        Assert.Equal(503, page.Status);
        Assert.Contains("El servicio no está disponible en este momento", page.Text);
        Assert.Empty(page.Select("[data-testid=company-row]"));
        WebFlows.AssertNoStackTrace(page);
    }

    public static TheoryData<string> FailureNames => FakeApi.RepresentativeBackendFailures;

    [Fact]
    public async Task A_refused_administrator_is_shown_the_forbidden_page_not_the_data()
    {
        using var harness = AdminHarness.Create(stub => stub.Overrides["GET /api/admin/tenants"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null));

        var page = await harness.OpenAsync("/admin");

        Assert.Equal(403, page.Status);
        Assert.Empty(page.Select("[data-testid=company-row]"));
    }

    // ---- the real API ----

    [Collection(nameof(WebIntegrationCollection))]
    public class WithTheRealApi(TestApiFactory api)
    {
        [Fact]
        public async Task A_new_company_appears_as_pending_and_changes_as_the_administrator_works()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "clínica", pending: true);
            using var client = await ApiSeed.AdminClientAsync(api, admin);
            var name = (await client.GetFromJsonAsync<Conversia.Application.DTOs.Admin.TenantDetailResponse>($"/api/admin/tenants/{owner.TenantId}"))!.Name;
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);
            var url = "/admin?buscar=" + Uri.EscapeDataString(name);

            var pending = (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
            var row = Assert.Single(pending.Select("[data-testid=company-row]"));
            Assert.Equal("Pending", row.GetAttribute("data-status"));
            Assert.Equal(owner.Email, row.QuerySelector("td[data-label=Propietario]")!.Plain().Trim());
            Assert.Equal("0 de 0", row.QuerySelector("[data-testid=company-lines]")!.Plain().Trim());
            Assert.Equal("Sin actividad", row.QuerySelector("[data-testid=company-activity]")!.Plain().Trim());
            Assert.NotNull(pending.Document.QuerySelector("[data-testid=pending-banner]"));

            await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active");
            await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 3);
            var active = Assert.Single((await session.GetAsync(url)).Select("[data-testid=company-row]"));
            Assert.Equal("Active", active.GetAttribute("data-status"));
            Assert.Equal("0 de 3", active.QuerySelector("[data-testid=company-lines]")!.Plain().Trim());
        }

        [Fact]
        public async Task Paging_works_against_the_real_list()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var token = Guid.NewGuid().ToString("N");
            using var anonymous = api.CreateClient();
            for (var i = 0; i < 21; i++)
            {
                var response = await anonymous.PostAsJsonAsync("/api/auth/register-tenant", new { tenantName = $"Spa {token} {i:00}", email = ApiSeed.UniqueEmail("spa"), password = ApiSeed.StrongPassword });
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }

            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var first = (await session.GetAsync($"/admin?buscar={token}")).ShouldBe(HttpStatusCode.OK);
            var second = (await session.GetAsync($"/admin?buscar={token}&pagina=2")).ShouldBe(HttpStatusCode.OK);

            Assert.Equal(20, first.Select("[data-testid=company-row]").Count());
            Assert.Single(second.Select("[data-testid=company-row]"));
            Assert.Contains("21 empresas", first.Document.QuerySelector("[data-testid=pager]")!.Plain());
        }
    }
}
