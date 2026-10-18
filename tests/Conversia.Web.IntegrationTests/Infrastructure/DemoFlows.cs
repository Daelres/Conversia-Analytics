using AngleSharp.Html.Dom;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>What the pages show about the demo accounts, read the way a person (or a scraper) would, plus the checks around it.</summary>
internal static class DemoFlows
{
    /// <summary>The live pages the public tour links to.</summary>
    public static readonly string[] LivePages = { "/login", "/register", "/users", "/products" };

    public static IReadOnlyList<string> LinkedPaths(PageResponse page)
        => page.Select("a[href]").Select(a => UiStructureAssertions.Resolve(page, a.GetAttribute("href"))).OfType<Uri>().Select(uri => uri.AbsolutePath).ToList();

    /// <summary>The structure of the real tour, with no wording pinned: a non-empty feature list and links to the live pages.</summary>
    public static void AssertIsTheRealTour(PageResponse page)
    {
        var features = page.Select(PageContract.TourFeature).ToList();
        Assert.True(features.Count > 0, $"{page.RequestUri.PathAndQuery} lists no {PageContract.TourFeature}: it is not the template tour.");
        Assert.All(features, f => Assert.False(string.IsNullOrWhiteSpace(f.TextContent), "A tour feature has no text."));
        var linked = LinkedPaths(page);
        foreach (var live in LivePages)
        {
            Assert.Contains(live, linked);
        }
    }

    /// <summary>The accounts of the table on <c>/demo</c>: email (<c>data-email</c>) and the password the page shows for the table.</summary>
    public static IReadOnlyList<string> TableEmails(PageResponse demoPage)
        => demoPage.Select(PageContract.DemoAccountRow).Select(r => r.GetAttribute("data-email") ?? string.Empty).ToList();

    public static IReadOnlyList<string> DisplayedPasswords(PageResponse demoPage)
        => demoPage.Select(PageContract.DemoPassword).Select(e => e.TextContent.Trim()).ToList();

    /// <summary>The shortcut forms of the "Cuentas de demostración" panel on <c>/login</c>.</summary>
    public static IReadOnlyList<IHtmlFormElement> ShortcutForms(PageResponse loginPage)
        => loginPage.Select(PageContract.DemoLoginPanel).SelectMany(panel => panel.QuerySelectorAll("form")).OfType<IHtmlFormElement>().ToList();

    public static IReadOnlyList<(string Email, string Password, FormModel Form, string ButtonText)> Shortcuts(PageResponse loginPage)
        => ShortcutForms(loginPage).Select(form =>
        {
            var model = FormModel.From(loginPage, form);
            var button = form.QuerySelector("button:not([type=button]):not([type=reset]), input[type=submit]");
            var text = button is IHtmlInputElement input ? input.Value : button?.TextContent ?? string.Empty;
            return (model.Get("email") ?? string.Empty, model.Get("password") ?? string.Empty, model, text.Trim());
        }).ToList();

    public static IHtmlFormElement ShortcutFor(PageResponse loginPage, string email)
        => Assert.Single(ShortcutForms(loginPage), f => FormModel.From(loginPage, f).Get("email") == email);

    /// <summary>With <c>Demo:ShowAccounts</c> off the page carries no demo hook and no account email or password at all.</summary>
    public static void AssertNoDemoAccountMaterial(PageResponse page, params string[] extraSecrets)
    {
        foreach (var hook in PageContract.DemoAccountHooks)
        {
            Assert.True(!page.Select(hook).Any(), $"{page.RequestUri.PathAndQuery} must not contain {hook} while Demo:ShowAccounts is off.");
        }

        foreach (var secret in DemoAccounts.Secrets.Concat(extraSecrets))
        {
            Assert.True(!page.Body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{page.RequestUri.PathAndQuery} must not contain '{secret}' while Demo:ShowAccounts is off.");
        }
    }

    /// <summary>The cookie attributes every sign-in must set, however the credentials arrived.</summary>
    public static void AssertHardenedAuthCookies(PageResponse signInResponse)
    {
        var cookies = WebFlows.IssuedCookies(signInResponse);
        Assert.NotEmpty(cookies);
        foreach (var cookie in cookies)
        {
            Assert.NotNull(WebFlows.Attribute(cookie, "httponly"));
            Assert.Equal("lax", WebFlows.Attribute(cookie, "samesite")?.ToLowerInvariant());
            Assert.NotNull(WebFlows.Attribute(cookie, "secure"));
            Assert.False(PageResponse.ContainsJwt(cookie), "The cookie value must not be a bare JWT.");
        }
    }
}
