extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// The structure rules of the shared interface (one heading, landmarks, labelled controls, tables that scroll inside their own region so the page
/// itself never scrolls sideways at 375 px, no inline layout) applied to the pages this feature added: the pending page, the line quota of the
/// organization page and the administration.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class AdminPageStructureTests(TestApiFactory api)
{
    [Fact]
    public async Task The_pages_of_the_feature_follow_the_structure_rules()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var pending = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: true);
        var active = await ApiSeed.RegisterOwnerAsync(api, "droguería");
        await ApiSeed.SetQuotaAsync(api, admin, active.TenantId, 1);
        var line = (await ApiSeed.CreateLinesAsync(api, active, "+57 300 777 0001"))[0];
        await ApiSeed.ActivateLineAsync(api, active, line);
        using var host = WebTestHost.Create(api);
        var pages = new List<(string Label, PageResponse Page, bool Shell)>();

        using (var anonymous = host.NewSession())
        {
            pages.Add(("/admin/login", (await anonymous.GetAsync("/admin/login")).ShouldBe(HttpStatusCode.OK), false));
        }

        using (var session = await WebFlows.SignedInAsync(host, pending.Email))
        {
            pages.Add(("/cuenta-pendiente", (await session.GetAsync("/cuenta-pendiente")).ShouldBe(HttpStatusCode.OK), true));
        }

        using (var session = await WebFlows.SignedInAsync(host, active.Email))
        {
            pages.Add(("/organizacion", (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK), true));
            pages.Add(("/organizacion/canales/{id}/activar", (await session.GetAsync($"/organizacion/canales/{line}/desactivar")).ShouldBe(HttpStatusCode.OK), true));
        }

        using (var session = await WebFlows.SignedInAdminAsync(host, admin))
        {
            foreach (var path in new[]
                     {
                         "/admin", $"/admin?buscar={Uri.EscapeDataString("droguería")}", "/admin/auditoria", $"/admin/auditoria?empresa={active.TenantId}",
                         $"/admin/empresas/{active.TenantId}", $"/admin/empresas/{active.TenantId}/estado/suspender", $"/admin/empresas/{active.TenantId}/lineas/{line}/desactivar",
                     })
            {
                pages.Add((path, (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK), true));
            }
        }

        var problems = new List<string>();
        foreach (var (label, page, shell) in pages)
        {
            problems.AddRange(PageStructureRules.Problems(label, page, shell));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
