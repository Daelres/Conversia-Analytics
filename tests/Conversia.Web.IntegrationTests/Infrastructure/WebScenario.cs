using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A Web host wired to a <see cref="FakeBackend"/> and an <see cref="ApiClientFaults"/>, with error reporting configured the way a
/// deployment does it (a shared key, the defaults for the rest). It starts in a second and shares nothing with other scenarios,
/// so the error-code tests run in parallel without a database.
/// </summary>
internal sealed class WebScenario : IDisposable
{
    /// <summary>A shared secret of 45 characters; the API side of a scenario that needs a real API is configured with the same one.</summary>
    public const string Key = "web-scenario-shared-secret-0123456789-abcdefg";

    public const string Password = "Sup3r-Secret!";

    private WebScenario(WebTestHost host, FakeBackend backend, ApiClientFaults faults, RequestProbe requests)
    {
        Host = host;
        Backend = backend;
        Faults = faults;
        Requests = requests;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public ApiClientFaults Faults { get; }

    /// <summary>Tells when a request has left the Web's pipeline and gives a fault the request it is failing.</summary>
    public RequestProbe Requests { get; }

    /// <param name="settings">Configuration by full key on top of <c>ErrorReporting:ApiKey</c> = <see cref="Key"/>; a null value removes the default.</param>
    public static WebScenario Create(params (string Key, string? Value)[] settings)
        => Create(new FakeBackend(), settings);

    public static WebScenario Create(FakeBackend backend, params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["ErrorReporting:ApiKey"] = Key };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var faults = new ApiClientFaults();
        var requests = new RequestProbe();
        var host = WebTestHost.CreateWithBackend(backend, new WebSettings(Config: values), services =>
        {
            faults.Install(services);
            requests.Install(services);
        });
        return new WebScenario(host, backend, faults, requests);
    }

    /// <summary>A browser tab that has signed in (through the Web's real login form) as <paramref name="email"/>.</summary>
    public Task<WebSession> SignInAsync(string email = "owner@example.test") => WebFlows.SignedInAsync(Host, email, Password);

    public WebSession Anonymous() => Host.NewSession();

    /// <summary>From now on every call the Web makes to its API client throws, as a bug in the Web would.</summary>
    public void BreakTheWeb(Func<Exception>? exception = null)
        => Faults.Fault = (_, _) => (exception ?? (() => new InvalidOperationException(SecretMessage)))();

    /// <summary>From now on every API call fails in the named way (see <see cref="FakeBackend.UpstreamFailures"/>).</summary>
    public void BreakTheApi(string failure) => Backend.Data = FakeBackend.UpstreamFailure(failure);

    public void BreakTheApi(Func<BackendRequest, Task<HttpResponseMessage?>> answer) => Backend.Data = answer;

    public void HealTheApi() => Backend.Data = null;

    /// <summary>The message of the exception <see cref="BreakTheWeb"/> throws: it must never reach a page.</summary>
    public const string SecretMessage = "boom-secret-detail-4711";

    public void Dispose() => Host.Dispose();
}
