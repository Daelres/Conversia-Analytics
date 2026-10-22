extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>The audit trail as a person reads it: what changed, who did it, when and for which company, with a filter by company and paging.</summary>
public class AdminAuditPageTests
{
    private static AdminHarness Harness(int extra = 0) => AdminHarness.Create(stub =>
    {
        stub.Audit.Add(AdminStub.Entry("tenant.status_changed", AdminStub.Aurora, """{"from":"Pending","to":"Active","reason":"Acuerdo firmado"}""", new DateTime(2026, 10, 20, 15, 5, 0, DateTimeKind.Utc)));
        stub.Audit.Add(AdminStub.Entry("tenant.quota_changed", AdminStub.Aurora, """{"from":0,"to":2}"""));
        stub.Audit.Add(AdminStub.Entry("channel.deactivated", AdminStub.Aurora, """{"channelId":"e0000000-0000-4000-8000-000000000001","displayPhoneNumber":"+57 300 000 0001"}"""));
        stub.Audit.Add(AdminStub.Entry("tenant.status_changed", AdminStub.Sur, """{"from":"Active","to":"Suspended","reason":null}"""));
        stub.Audit.Add(AdminStub.Entry("something.new", Guid.NewGuid(), """{"x":1}"""));
        stub.Audit.Add(AdminStub.Entry("tenant.quota_changed", AdminStub.Norte, "[1,2]")); // a body that is not an object
    });

    [Fact]
    public async Task Every_kind_of_change_is_written_in_words_with_who_when_and_which_company()
    {
        using var harness = Harness();

        var page = (await harness.OpenAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Auditoría", page.Document.QuerySelector("main h1")!.Plain().Trim());
        var entries = page.Select("[data-testid=audit-entry]").ToList();
        Assert.Equal(6, entries.Count);

        var status = entries[0];
        Assert.Equal("Cambió el estado: Pendiente → Activa", status.QuerySelector(".audit__title")!.Plain().Trim());
        Assert.Equal("Motivo: Acuerdo firmado", status.QuerySelector(".audit__detail")!.Plain().Trim());
        Assert.Equal("20 oct 2026, 10:05", status.QuerySelector("time")!.Plain().Trim());
        Assert.Equal("2026-10-20T15:05:00.0000000Z", status.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Contains(AdminStub.Email, status.QuerySelector(".audit__meta")!.Plain());
        var company = status.QuerySelector(".audit__meta a")!;
        Assert.Equal("Depilación Aurora", company.Plain().Trim());
        Assert.Equal($"/admin/empresas/{AdminStub.Aurora}", company.GetAttribute("href"));

        Assert.Equal("Cambió el cupo de líneas: 0 → 2", entries[1].QuerySelector(".audit__title")!.Plain().Trim());
        Assert.Equal("Desactivó una línea", entries[2].QuerySelector(".audit__title")!.Plain().Trim());
        Assert.Equal("Número: +57 300 000 0001", entries[2].QuerySelector(".audit__detail")!.Plain().Trim());
        Assert.Equal("Cambió el estado: Activa → Suspendida", entries[3].QuerySelector(".audit__title")!.Plain().Trim());
        Assert.Empty(entries[3].QuerySelectorAll(".audit__detail")); // no reason typed: no empty line
    }

    [Fact]
    public async Task An_action_the_page_does_not_know_and_a_company_it_cannot_name_are_still_shown()
    {
        using var harness = Harness();

        var page = (await harness.OpenAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);

        var entries = page.Select("[data-testid=audit-entry]").ToList();
        Assert.Equal("Cambio registrado (something.new)", entries[4].QuerySelector(".audit__title")!.Plain().Trim());
        Assert.Matches(@"Empresa [0-9a-f]{8}\b", entries[4].QuerySelector(".audit__meta a")!.Plain());
        Assert.Equal("Cambió el cupo de líneas: — → —", entries[5].QuerySelector(".audit__title")!.Plain().Trim());
    }

    [Fact]
    public async Task The_raw_details_of_a_change_are_never_printed()
    {
        using var harness = Harness();

        var page = (await harness.OpenAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);

        Assert.DoesNotContain("channelId", page.Body);
        Assert.DoesNotContain("e0000000-0000-4000-8000-000000000001", page.Body);
        Assert.DoesNotContain("displayPhoneNumber", page.Body);
    }

    [Fact]
    public async Task The_company_filter_lists_the_companies_and_is_sent_to_the_api()
    {
        using var harness = Harness();

        var page = (await harness.OpenAsync($"/admin/auditoria?empresa={AdminStub.Aurora}")).ShouldBe(HttpStatusCode.OK);

        var options = page.Select("#f-company option").ToList();
        Assert.Equal("Todas", options[0].Plain().Trim());
        Assert.Equal(["Clínica <b>Sur</b>", "Depilación Aurora", "Estética Norte"], options.Skip(1).Select(o => o.Plain().Trim()));
        Assert.Equal(AdminStub.Aurora.ToString(), Assert.Single(options, o => o.HasAttribute("selected")).GetAttribute("value"));
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/admin/auditoria" && a.Plain().Contains("Borrar filtro"));
        var call = harness.Backend.DataRequests.Single(r => r.Path == "/api/admin/audit");
        Assert.Equal(AdminStub.Aurora.ToString(), System.Web.HttpUtility.ParseQueryString(call.Uri.Query)["tenantId"]);
        Assert.Equal("20", System.Web.HttpUtility.ParseQueryString(call.Uri.Query)["pageSize"]);
    }

    [Theory]
    [InlineData("empresa=no-es-un-guid")]
    [InlineData("empresa=00000000-0000-0000-0000-000000000000")]
    [InlineData("pagina=0")]
    [InlineData("pagina=x")]
    public async Task A_filter_value_that_is_not_valid_is_ignored(string query)
    {
        using var harness = Harness();

        var page = (await harness.OpenAsync("/admin/auditoria?" + query)).ShouldBe(HttpStatusCode.OK);

        var call = harness.Backend.DataRequests.Single(r => r.Path == "/api/admin/audit");
        var sent = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Null(sent["tenantId"]);
        Assert.Equal("1", sent["page"]);
        Assert.Equal(6, page.Select("[data-testid=audit-entry]").Count());
    }

    [Fact]
    public async Task Without_changes_the_page_says_so()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Sin cambios registrados", page.Document.QuerySelector("[data-testid=empty-state] .empty__title")!.Plain().Trim());
        Assert.Empty(page.Select("[data-testid=audit-list]"));
    }

    [Fact]
    public async Task A_company_name_is_shown_as_text_even_when_it_looks_like_markup()
    {
        using var harness = AdminHarness.Create(stub => stub.Audit.Add(AdminStub.Entry("tenant.quota_changed", AdminStub.Sur, """{"from":1,"to":4}""")));

        var page = (await harness.OpenAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Clínica <b>Sur</b>", page.Document.QuerySelector(".audit__meta a")!.Plain().Trim());
        Assert.Empty(page.Select(".audit__meta b"));
    }

    [Fact]
    public async Task When_the_names_cannot_be_had_the_trail_still_reads_with_short_ids()
    {
        using var harness = Harness();
        using var session = await harness.SignInAsync();
        harness.Stub.Overrides["GET /api/admin/tenants"] = _ => FakeBackend.Raw(HttpStatusCode.InternalServerError, """{"title":"x","status":500}""");

        var page = await session.GetAsync("/admin/auditoria");

        // The names are a courtesy: the API answered the trail, so the page stands and the companies are shown by a short id.
        Assert.Equal(200, page.Status);
        Assert.Contains($"Empresa {AdminStub.Aurora.ToString()[..8]}", page.Text);
    }

    [Fact]
    public async Task A_backend_that_fails_is_said_plainly()
    {
        using var harness = Harness();
        using var session = await harness.SignInAsync();
        harness.Host.Api.Intercept = FakeApi.Failure("connection refused");

        var page = await session.GetAsync("/admin/auditoria");

        Assert.Equal(503, page.Status);
        Assert.Contains("El servicio no está disponible en este momento", page.Text);
        Assert.Empty(page.Select("[data-testid=audit-entry]"));
    }

    // ---- the real API ----

    [Collection(nameof(WebIntegrationCollection))]
    public class WithTheRealApi(TestApiFactory api)
    {
        [Fact]
        public async Task The_changes_made_to_a_company_show_up_newest_first_and_only_for_that_company_when_filtered()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var mine = await ApiSeed.RegisterOwnerAsync(api, "estética", pending: true);
            var other = await ApiSeed.RegisterOwnerAsync(api, "droguería", pending: true);
            await ApiSeed.SetStatusAsync(api, admin, mine.TenantId, "Active", "Acuerdo firmado");
            await ApiSeed.SetQuotaAsync(api, admin, mine.TenantId, 3);
            await ApiSeed.SetStatusAsync(api, admin, other.TenantId, "Suspended");
            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var filtered = (await session.GetAsync($"/admin/auditoria?empresa={mine.TenantId}")).ShouldBe(HttpStatusCode.OK);
            var titles = filtered.Select("[data-testid=audit-entry] .audit__title").Select(t => t.Plain().Trim()).ToList();
            Assert.Equal(["Cambió el cupo de líneas: 0 → 3", "Cambió el estado: Pendiente → Activa"], titles);
            Assert.Contains("Motivo: Acuerdo firmado", filtered.Text);

            var all = (await session.GetAsync("/admin/auditoria")).ShouldBe(HttpStatusCode.OK);
            Assert.Contains(all.Select("[data-testid=audit-entry]"), e => e.Plain().Contains("Pendiente → Suspendida"));
        }

        [Fact]
        public async Task Paging_the_trail_works()
        {
            var admin = await ApiSeed.CreateAdminAsync(api);
            var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería");
            for (var i = 1; i <= 21; i++)
            {
                await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, i);
            }

            using var host = WebTestHost.Create(api);
            using var session = await WebFlows.SignedInAdminAsync(host, admin);

            var first = (await session.GetAsync($"/admin/auditoria?empresa={owner.TenantId}")).ShouldBe(HttpStatusCode.OK);
            var second = (await session.GetAsync($"/admin/auditoria?empresa={owner.TenantId}&pagina=2")).ShouldBe(HttpStatusCode.OK);

            Assert.Equal(20, first.Select("[data-testid=audit-entry]").Count());
            Assert.Single(second.Select("[data-testid=audit-entry]"));
            Assert.Contains("21 cambios", first.Document.QuerySelector("[data-testid=pager]")!.Plain());
            Assert.Equal($"/admin/auditoria?empresa={owner.TenantId}&pagina=2", first.Document.QuerySelector("a[data-testid=pager-next]")!.GetAttribute("href"));
        }
    }
}
