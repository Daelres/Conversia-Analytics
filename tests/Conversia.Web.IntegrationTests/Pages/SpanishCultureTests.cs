extern alias ApiHost;

using System.Net;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The Web always renders in the <c>es</c> culture, whatever the browser asks for: <c>Accept-Language</c>, a culture cookie or a query
/// string change nothing.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class SpanishCultureTests(TestApiFactory api)
{
    // ---- the culture is always es ----

    public static TheoryData<string, string> RequestedCultures => new()
    {
        { "Accept-Language", "en-US" },
        { "Accept-Language", "en-US,en;q=0.9" },
        { "Accept-Language", "en-GB" },
        { "Accept-Language", "fr-FR,fr;q=0.8" },
        { "Accept-Language", "de-DE" },
        { "Accept-Language", "ja" },
        { "Accept-Language", "es-AR" },
        { "Accept-Language", "es-MX" },
        { "Accept-Language", "*" },
        { "Accept-Language", "zz-ZZ" },
        { "Cookie", ".AspNetCore.Culture=c%3Den-US%7Cuic%3Den-US" },
    };

    [Theory]
    [MemberData(nameof(RequestedCultures))]
    public async Task Whatever_culture_the_browser_asks_for_the_pages_are_spanish(string header, string value)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var report = new SpanishUiReport();

        var login = (await host.NewSession().GetAsync("/login", (header, value))).ShouldBe(HttpStatusCode.OK);
        var users = (await session.GetAsync("/users", (header, value))).ShouldBe(HttpStatusCode.OK);
        var queryOverride = (await session.GetAsync("/users?culture=en-US&ui-culture=en-US", (header, value))).ShouldBe(HttpStatusCode.OK);

        report.Check($"login with {header}: {value}", login);
        report.Check($"users with {header}: {value}", users);
        report.Check($"users with ?culture=en-US and {header}: {value}", queryOverride);
        report.AssertClean();
        foreach (var page in new[] { login, users, queryOverride })
        {
            Assert.Equal("es", page.Document.DocumentElement.GetAttribute("lang"));
        }
    }
}
