using System.Net;
using Conversia.Web.Http;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Security;

/// <summary>The security headers of the Web (docs/BILLING.md, "Adding a payment method"): everywhere, and the strict policy where card data is typed.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class SecurityHeadersTests(TestApiFactory api)
{
    [Fact]
    public async Task Every_page_forbids_framing_sniffing_and_foreign_form_targets()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("nosniff", page.Header("X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", page.Header("Referrer-Policy"));
        var policy = page.Header("Content-Security-Policy")!;
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("form-action 'self';", policy);
        Assert.DoesNotContain("wompi", policy);
    }

    [Fact]
    public async Task The_payment_method_page_runs_only_this_sites_scripts_and_talks_only_to_wompi()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "csp");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await session.GetAsync(SecurityHeadersMiddleware.PaymentMethodPath);

        var policy = page.Header("Content-Security-Policy")!;
        Assert.Contains("connect-src 'self' https://sandbox.wompi.co https://production.wompi.co;", policy);
        var nonce = Assert.Single(page.Select("head script[nonce]")).GetAttribute("nonce");
        Assert.Contains($"script-src 'self' 'nonce-{nonce}';", policy);
        Assert.DoesNotContain("unsafe-inline';", policy.Split("script-src")[1].Split(';')[0]);

        var billing = await session.GetAsync(SecurityHeadersMiddleware.BillingPath);
        Assert.Contains("form-action 'self' https://checkout.wompi.co;", billing.Header("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("https://sandbox.wompi.co/v1", true)]
    [InlineData("https://production.wompi.co/v1", true)]
    [InlineData("https://evil.example/v1", false)]
    [InlineData("http://sandbox.wompi.co/v1", false)]
    [InlineData("https://sandbox.wompi.co.evil.example/v1", false)]
    [InlineData(null, false)]
    public void Only_the_two_wompi_api_bases_reach_the_browser(string? url, bool allowed)
        => Assert.Equal(allowed, SecurityHeadersMiddleware.IsWompiApiBase(url));

    [Fact]
    public void The_browser_script_accepts_only_the_two_wompi_api_bases()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.WebProject(), "wwwroot", "js", "billing.js"));

        Assert.Contains("var WOMPI_BASES = ['https://sandbox.wompi.co/v1', 'https://production.wompi.co/v1'];", script);
        Assert.Contains("if (WOMPI_BASES.indexOf(form.dataset.apiBase) < 0) { throw new Error('host'); }", script);
        Assert.Contains("if (WOMPI_BASES.indexOf(form.dataset.apiBase) < 0) { return; }", script);
    }
}
