namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The structure rules of the shared interface (one heading, landmarks, labelled controls, tables that scroll inside their own region so the
/// page itself never scrolls sideways at 375 px, no inline layout), applied to one page. Returns what is wrong, empty when nothing is.
/// </summary>
internal static class PageStructureRules
{
    public static List<string> Problems(string label, PageResponse page, bool shell)
    {
        var problems = new List<string>();
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

        return problems;
    }
}
