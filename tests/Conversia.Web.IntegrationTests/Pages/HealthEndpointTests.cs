extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET /health is the container liveness probe: anonymous, a bare status word and no call to the API.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class HealthEndpointTests(TestApiFactory api)
{
    [Fact]
    public async Task Health_is_anonymous_returns_only_the_status_word_and_does_not_call_the_api()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = (await session.GetAsync("/health")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Healthy", response.Body.Trim());
        Assert.StartsWith("text/plain", response.Header("Content-Type"));
        Assert.Contains("no-store", response.Header("Cache-Control"));
        Assert.Equal(0, host.Api.CallCount);
    }
}
