using System.Diagnostics;
using System.Runtime.CompilerServices;
using Conversia.Web.ErrorReporting;
using Conversia.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The Web's error reporting, composed exactly as the Web registers it (<c>AddApiClient</c> + <c>AddWebErrorReporting</c>) in a plain
/// service provider, with a <see cref="FakeBackend"/> as the only thing it can talk to: no host, no pipeline, no database.
/// Used for what the reporter does on its own; the pages and the exception handler are tested through the real host.
/// </summary>
internal sealed class ReporterHarness : IDisposable
{
    /// <summary>A shared secret of 44 characters, as the requirement wants (at least 32).</summary>
    public const string Key = "reporter-tests-shared-secret-0123456789-abcdef";

    public const string EnvironmentName = "Staging";

    private readonly ServiceProvider _provider;

    private ReporterHarness(ServiceProvider provider, FakeBackend backend, CapturedLogs logs)
    {
        _provider = provider;
        Backend = backend;
        Logs = logs;
        Reporter = provider.GetRequiredService<IWebErrorReporter>();
    }

    public FakeBackend Backend { get; }

    public CapturedLogs Logs { get; }

    public IWebErrorReporter Reporter { get; }

    /// <summary>The clock the reporter was given (null: the system's).</summary>
    public ManualTimeProvider? Clock { get; private set; }

    public IServiceProvider Services => _provider;

    /// <param name="settings">Configuration by full key on top of the defaults (<c>Api:BaseUrl</c> and <c>ErrorReporting:ApiKey</c> = <see cref="Key"/>); a null value removes the default.</param>
    public static ReporterHarness Create(params (string Key, string? Value)[] settings)
        => Build(null, settings);

    /// <summary>
    /// Like <see cref="Create"/>, with <paramref name="clock"/> registered as the <see cref="TimeProvider"/> of the container, which is
    /// where the reporter (and its storm control) must get its time from: <c>TryAddSingleton(TimeProvider.System)</c> in the Web's
    /// registration, a <see cref="TimeProvider"/> constructor parameter in the classes that need one.
    /// </summary>
    public static ReporterHarness CreateWithClock(ManualTimeProvider clock, params (string Key, string? Value)[] settings)
        => Build(clock, settings);

    private static ReporterHarness Build(ManualTimeProvider? clock, (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Api:BaseUrl"] = WebTestHost.ApiBaseUrl,
            ["ErrorReporting:ApiKey"] = Key,
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var backend = new FakeBackend();
        var logs = new CapturedLogs();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(EnvironmentName));
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(logs);
        });
        if (clock is not null)
        {
            services.AddSingleton<TimeProvider>(clock);
        }

        services.AddHttpContextAccessor();
        services.AddApiClient();
        services.AddWebErrorReporting();
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.PrimaryHandler = backend));

        return new ReporterHarness(services.BuildServiceProvider(), backend, logs) { Clock = clock };
    }

    /// <summary>An exception that has really been thrown (so that it has a stack trace), from a method of the test assembly.</summary>
    public static Exception Thrown(string message = "something broke", Exception? inner = null)
    {
        try
        {
            ThrowInvalidOperation(message, inner);
            return null!;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOperation(string message, Exception? inner = null) => throw new InvalidOperationException(message, inner);

    /// <summary>The same type thrown from another method: a different defect for the storm guard (the fingerprint includes the top application frame).</summary>
    public static Exception ThrownElsewhere(string message = "something else broke")
    {
        try
        {
            ThrowInvalidOperationElsewhere(message);
            return null!;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperationElsewhere(string message) => throw new InvalidOperationException(message);

    public static DefaultHttpContext Request(string method = "GET", string path = "/products", string query = "", string? userAgent = "tests/1.0", string? traceIdentifier = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        context.TraceIdentifier = traceIdentifier ?? Guid.NewGuid().ToString("N");
        return context;
    }

    public void Dispose() => _provider.Dispose();

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Conversia.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
