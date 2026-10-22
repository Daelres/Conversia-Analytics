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
            var document = page.Document;
            void Check(bool ok, string what)
            {
                if (!ok)
                {
                    problems.Add($"{label}: {what}");
                }
            }

            Check(document.DocumentElement.GetAttribute("lang") == "es", "<html lang=\"es\">");
            Check(!string.IsNullOrWhiteSpace(document.Title), "a title");
            Check(document.QuerySelectorAll("main#main").Length == 1, "exactly one main#main");
            Check(document.QuerySelectorAll("main h1").Length == 1, "exactly one h1 in main");
            Check(document.QuerySelector("a.skip-link[href$='#main']") is not null, "a skip link");
            Check(document.QuerySelector("[data-page]") is not null, "a data-page hook");
            Check(!shell || document.QuerySelectorAll("nav[aria-label]").Length >= 1, "a labelled navigation");
            Check(document.QuerySelectorAll("[style]").Length == 0, "no inline styles (layout lives in the stylesheets)");
            var ids = document.QuerySelectorAll("[id]").Select(e => e.Id).ToList();
            Check(ids.Count == ids.Distinct().Count(), $"unique ids ({string.Join(", ", ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key))})");

            foreach (var table in document.QuerySelectorAll("table"))
            {
                var wrap = table.Closest(".table-wrap");
                Check(wrap is { } && wrap.GetAttribute("role") == "region" && wrap.HasAttribute("aria-label") && wrap.GetAttribute("tabindex") == "0", "every table in a labelled, focusable .table-wrap (it scrolls inside, not the page)");
                Check(table.QuerySelector("caption") is not null, "a table caption");
                Check(table.QuerySelectorAll("thead th").All(th => th.GetAttribute("scope") == "col"), "column headers with scope");
            }

            foreach (var control in document.QuerySelectorAll("input:not([type=hidden]):not([type=checkbox]), select, textarea"))
            {
                var id = control.Id;
                Check(!string.IsNullOrEmpty(id) && document.QuerySelector($"label[for='{id}']") is not null, $"a label for the control {control.GetAttribute("name")}");
            }

            foreach (var form in document.Forms.Where(f => string.Equals(f.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase)))
            {
                Check(form.QuerySelector("input[name=__RequestVerificationToken]") is not null, $"an antiforgery token in the form to {form.GetAttribute("action") ?? "(self)"}");
            }

            foreach (var link in document.QuerySelectorAll("main a[href], nav a[href]"))
            {
                Check(!string.IsNullOrWhiteSpace(link.TextContent) || link.HasAttribute("aria-label"), $"link text for {link.GetAttribute("href")}");
            }

            foreach (var button in document.QuerySelectorAll("button"))
            {
                Check(!string.IsNullOrWhiteSpace(button.TextContent) || button.HasAttribute("aria-label"), "button text");
            }

            foreach (var svg in document.QuerySelectorAll("svg[role=img]"))
            {
                Check(svg.HasAttribute("aria-label"), "a name for every informative svg");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
