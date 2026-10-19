extern alias ApiHost;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A booted Conversia.Web whose API traffic goes — through a recording handler — to the real
/// in-process API (<see cref="TestApiFactory"/>). The Web runs with the Production pipeline so the
/// real exception handler / error page is under test.
/// </summary>
internal sealed class WebTestHost : IDisposable
{
    public static readonly Uri BaseAddress = new("https://localhost");
    public const string ApiBaseUrl = "http://api.test/";

    private readonly WebApplicationFactory<Conversia.Web.Components.App> _factory;
    private readonly List<WebSession> _sessions = new();

    private WebTestHost(WebApplicationFactory<Conversia.Web.Components.App> factory, RecordingApiHandler api, CapturedLogs logs)
    {
        _factory = factory;
        Api = api;
        Logs = logs;
    }

    public RecordingApiHandler Api { get; }

    public CapturedLogs Logs { get; }

    public IServiceProvider Services => _factory.Services;

    /// <param name="apiHost">The API host to reach; defaults to the shared, unthrottled one.</param>
    /// <param name="settings">The Web's own hosting environment and configuration; defaults to Production with nothing set.</param>
    /// <param name="configureServices">Replaces or adds services of the Web (for example a stand-in for <c>IApiClient</c>), applied after the Web's own registrations.</param>
    public static WebTestHost Create(TestApiFactory api, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ApiHost::Program>? apiHost = null, WebSettings? settings = null, Action<IServiceCollection>? configureServices = null)
        => Create((apiHost ?? api).Server.CreateHandler(), settings, configureServices);

    /// <summary>A Web that has no backend at all: for tests of what happens while the host boots (or refuses to).</summary>
    public static WebTestHost CreateWithoutApi(WebSettings settings)
        => Create(new NoBackendHandler(), settings);

    /// <summary>A Web whose whole backend is <paramref name="backend"/> (a stand-in for the API: no database, no container).</summary>
    public static WebTestHost CreateWithBackend(HttpMessageHandler backend, WebSettings? settings = null, Action<IServiceCollection>? configureServices = null)
        => Create(backend, settings, configureServices);

    private static WebTestHost Create(HttpMessageHandler apiHandler, WebSettings? settings, Action<IServiceCollection>? configureServices = null)
    {
        var recorder = new RecordingApiHandler(apiHandler);
        var logs = new CapturedLogs();

        var factory = new WebFactory(recorder, logs, settings ?? new WebSettings(), configureServices);
        return new WebTestHost(factory, recorder, logs);
    }

    /// <summary>A browser: its own cookie jar, no automatic redirects.</summary>
    public WebSession NewSession()
    {
        var jar = new CookieContainer();
        var client = _factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(jar));
        var session = new WebSession(client, jar);
        lock (_sessions)
        {
            _sessions.Add(session);
        }

        return session;
    }

    public void Dispose()
    {
        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                session.Dispose();
            }
        }

        _factory.Dispose();
    }

    private sealed class NoBackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no backend in this test host");
    }

    private sealed class WebFactory(RecordingApiHandler api, CapturedLogs logs, WebSettings settings, Action<IServiceCollection>? configureServices) : WebApplicationFactory<Conversia.Web.Components.App>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(settings.Environment);
            // A host that is built and abandoned (a boot the Web refuses) would keep one inotify instance per watched file.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            builder.UseSetting("Api:BaseUrl", ApiBaseUrl);
            foreach (var (key, value) in settings.Config ?? new Dictionary<string, string?>())
            {
                if (value is not null)
                {
                    builder.UseSetting(key, value);
                }
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                // The Web's appsettings.json carries filter rules (Default: Information, Microsoft.AspNetCore: Warning)
                // that out-rank SetMinimumLevel, so drop every rule after all other configuration has run: the
                // capture must see Debug/Trace entries too, or "no secret is ever logged" would be weaker than it reads.
                services.PostConfigure<LoggerFilterOptions>(options =>
                {
                    options.Rules.Clear();
                    options.MinLevel = LogLevel.Trace;
                });
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.PrimaryHandler = api));
                configureServices?.Invoke(services);
            });
        }
    }
}

/// <summary>What the Web host under test is configured with. <c>null</c> means "the setting is not supplied at all".</summary>
/// <param name="Config">Any other setting by its full key (<c>ErrorReporting:ApiKey</c>...); a null value means "not supplied".</param>
internal sealed record WebSettings(string Environment = "Production", IReadOnlyDictionary<string, string?>? Config = null)
{
    /// <summary>Production with these settings only.</summary>
    public static WebSettings With(params (string Key, string? Value)[] settings)
        => new(Config: settings.ToDictionary(s => s.Key, s => s.Value));
}
