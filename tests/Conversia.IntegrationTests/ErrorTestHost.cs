using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Text;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Products;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;

namespace Conversia.IntegrationTests;

/// <summary>
/// The real API on the shared PostgreSQL, plus what the error registry tests need around it: a log capture that sees
/// structured properties, a way to make a request fail on purpose (headers <see cref="FaultInjection.FaultHeader"/> and
/// <see cref="FaultInjection.MessageHeader"/> on a request that is otherwise real), and a recorder of what the pipeline
/// wrote back. Hosts are derived from the shared factory, so they run on the same container.
/// </summary>
internal sealed class ErrorTestHost
{
    /// <summary>The 40-character report key the main host is configured with.</summary>
    public const string ReportKey = "report-key-for-the-tests-0123456789-abcdefghij";

    public const string ReportUrl = "/api/diagnostics/errors";

    private ErrorTestHost(
        WebApplicationFactory<Program> factory, LogCapture logs, RequestRecorder recorder, AbortSignals signals, CommandInterceptor commands, string connectionString)
    {
        Factory = factory;
        Logs = logs;
        Recorder = recorder;
        Signals = signals;
        Commands = commands;
        ConnectionString = connectionString;
    }

    public WebApplicationFactory<Program> Factory { get; }

    public LogCapture Logs { get; }

    public RequestRecorder Recorder { get; }

    public AbortSignals Signals { get; }

    /// <summary>Every database command this host sent (its own counter, so other hosts and background work elsewhere do not count).</summary>
    public CommandInterceptor Commands { get; }

    /// <summary>The database this host stores its error rows in, for reading them back.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// The shared host: the shared database, a configured report key, an effectively unlimited storm budget and report budget,
    /// and the real services wrapped so a request can be made to fail.
    /// </summary>
    public static ErrorTestHost Default(CustomWebApplicationFactory shared)
        => shared.GetOrAddHost("error-registry:main", () => Create(shared, Settings(
            ("ErrorLogging:ReportKey", ReportKey),
            ("ErrorLogging:ReportRateLimitPerMinute", "1000000"))));

    /// <summary>A host with its own settings (on top of the shared host's), optionally on another database or with extra services.</summary>
    public static ErrorTestHost Create(
        CustomWebApplicationFactory shared,
        IReadOnlyDictionary<string, string> settings,
        string? connectionString = null,
        Action<IServiceCollection, LogCapture>? configureServices = null,
        Action<IWebHostBuilder>? configureHost = null)
    {
        var logs = new LogCapture();
        var recorder = new RequestRecorder();
        var signals = new AbortSignals();
        var commands = new CommandInterceptor { Match = string.Empty };
        var factory = shared.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            }

            // Hosts that are built and abandoned would otherwise keep a file watcher each (Linux allows 128 inotify instances).
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            builder.ConfigureLogging(logging =>
            {
                logging.Services.Configure<LoggerFilterOptions>(options => options.Rules.Clear());
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            });
            builder.ConfigureServices(services =>
            {
                FaultInjection.Install(services, signals);
                services.AddSingleton<IStartupFilter>(new RequestRecorder.Filter(recorder));
                ReplaceDatabase(services, connectionString ?? shared.ServerConnectionString, commands);

                configureServices?.Invoke(services, logs);
            });
            configureHost?.Invoke(builder);
        });

        return new ErrorTestHost(factory, logs, recorder, signals, commands, connectionString ?? shared.ServerConnectionString);
    }

    /// <summary>
    /// A host on a migrated, empty database of its own (so no other host or test writes to it) whose retention is far too long to
    /// delete anything. Returns once that host's startup purge has (very probably) finished. The wait is only a courtesy: the tests that
    /// count commands read <see cref="CommandInterceptor.CommandsExceptRetentionPurge"/>, so a purge that is late cannot change a count.
    /// </summary>
    public static async Task<OwnDatabaseHost> StartOnOwnDatabaseAsync(CustomWebApplicationFactory shared)
    {
        var database = DemoDatabase.Create(shared);
        ErrorTestHost? host = null;
        try
        {
            await database.MigrateAsync();
            host = Create(shared, Settings(("ErrorLogging:RetentionDays", "3650")), database.ConnectionString);
            await host.WaitForStartupPurgeAsync();
            return new OwnDatabaseHost(host, database);
        }
        catch
        {
            // A setup that throws never reaches the caller's `await using`, so the host and the database are released here.
            host?.Factory.Dispose();
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Boots the host and waits (bounded) until the retention service has finished its startup purge: the first command on
    /// <c>ErrorLogs</c> has been seen (or none comes within a few seconds) and the host's database has then been quiet for a while.
    /// There is no log or state signal to wait on, and <c>TrackCurrentFlow</c> does not follow a request through TestServer, so
    /// nothing may depend on this wait for correctness (see <see cref="CommandInterceptor.CommandsExceptRetentionPurge"/>).
    /// </summary>
    public async Task WaitForStartupPurgeAsync()
    {
        _ = Factory.Services;
        var firstSeen = DateTime.UtcNow.AddSeconds(10);
        while (Commands.Commands.Count == 0 && DateTime.UtcNow < firstSeen)
        {
            await Task.Delay(50);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var lastCount = -1;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var count = Commands.Commands.Count;
            if (count != lastCount)
            {
                lastCount = count;
                quietSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(750))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new XunitException("The host kept sending database commands for 30 seconds after it started: its startup purge never finished.");
    }

    public static Dictionary<string, string> Settings(params (string Key, string Value)[] settings)
        => settings.ToDictionary(s => s.Key, s => s.Value);

    /// <summary>Points the host's <see cref="ApplicationDbContext"/> at another database, optionally with an interceptor.</summary>
    public static void ReplaceDatabase(IServiceCollection services, string connectionString, params IInterceptor[] interceptors)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString).AddInterceptors(interceptors));
    }

    // ---- requests ----

    public HttpClient NewClient(string? bearerToken = null, string? clientIp = null)
    {
        var client = Factory.CreateClient();
        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (clientIp is not null)
        {
            client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, clientIp);
        }

        return client;
    }

    /// <summary>A never-used marker; sent as the user agent so that the rows of one test are found without sharing a table with the others.</summary>
    public static string NewMarker() => $"errreg-{Guid.NewGuid():N}";

    /// <summary>A real, valid <c>POST /api/auth/login</c> that fails inside the use case with the chosen <paramref name="fault"/>.</summary>
    public static HttpRequestMessage FailingLogin(string marker, string fault = FaultInjection.InvalidOperation, string? message = null, string? callId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent($$"""{"email":"{{TestHelpers.UniqueEmail("boom")}}","password":"{{TestHelpers.StrongPassword}}"}""", Encoding.UTF8, "application/json"),
        };
        return Tag(request, marker, fault, message, callId);
    }

    /// <summary>
    /// A real, valid <c>PUT /api/products/{id}</c> (owner token) that fails inside the use case. Its body, headers, cookie and
    /// query carry unique secrets the registry must never store.
    /// </summary>
    public static HttpRequestMessage FailingProductUpdate(
        Guid productId, string marker, string token, string fault = FaultInjection.InvalidOperation, string? message = null, string query = "", string? callId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/products/{productId}{query}")
        {
            Content = new StringContent(
                $$"""{"name":"Yunque BODY-NAME-{{marker}}","price":10.5,"stock":3,"rowVersion":"AAAAAAAAB9E=","note":"BODY-SECRET-{{marker}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Tag(request, marker, fault, message, callId);
    }

    private static HttpRequestMessage Tag(HttpRequestMessage request, string marker, string fault, string? message, string? callId)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", marker);
        request.Headers.TryAddWithoutValidation(FaultInjection.FaultHeader, fault);
        request.Headers.TryAddWithoutValidation(FaultInjection.MessageHeader, message ?? $"forced failure {marker}");
        request.Headers.TryAddWithoutValidation(RequestRecorder.CallHeader, callId ?? marker);
        return request;
    }

    // ---- registry access ----

    /// <summary>Runs <paramref name="use"/> with the host's registry resolved from a fresh scope (whatever its lifetime).</summary>
    public async Task<T> WithRegistryAsync<T>(Func<IErrorRegistry, Task<T>> use)
    {
        using var scope = Factory.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<IErrorRegistry>());
    }

    public async Task<T> WithPurgerAsync<T>(Func<IErrorLogPurger, Task<T>> use)
    {
        using var scope = Factory.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<IErrorLogPurger>());
    }
}

/// <summary>An <see cref="ErrorTestHost"/> together with the throw-away database it runs on; disposing drops both.</summary>
internal sealed class OwnDatabaseHost(ErrorTestHost host, DemoDatabase database) : IAsyncDisposable
{
    public ErrorTestHost Host { get; } = host;

    public async ValueTask DisposeAsync()
    {
        Host.Factory.Dispose();
        await database.DisposeAsync();
    }
}

/// <summary>Signals "the request reached the use case", so a test can cancel it at a known point instead of sleeping.</summary>
internal sealed class AbortSignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();

    private TaskCompletionSource For(string key)
        => _started.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void MarkStarted(string key) => For(key).TrySetResult();

    public Task WaitStartedAsync(string key) => For(key).Task.WaitAsync(TimeSpan.FromSeconds(30));
}

/// <summary>What the pipeline did with a request, as seen from outside the API's own middleware.</summary>
internal sealed class RecordedRequest
{
    public string? TraceIdentifier { get; set; }

    public int StatusCode { get; set; }

    public string Body { get; set; } = string.Empty;

    public bool HasStarted { get; set; }

    /// <summary>The response carries an <c>X-Error-Id</c> header.</summary>
    public bool ErrorIdHeader { get; set; }

    /// <summary>The connection was aborted (<c>HttpContext.Abort</c>) by the time the API's pipeline returned.</summary>
    public bool Aborted { get; set; }

    public Exception? Escaped { get; set; }

    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A pipeline step placed ahead of the API's whole pipeline (like a reverse proxy would be) that records, for requests carrying
/// <see cref="CallHeader"/>, the request id, the final status, every byte written and whether an exception escaped the API.
/// </summary>
internal sealed class RequestRecorder
{
    public const string CallHeader = "X-Test-Call";

    private readonly ConcurrentDictionary<string, RecordedRequest> _requests = new();

    public RecordedRequest For(string callId) => _requests.GetOrAdd(callId, _ => new RecordedRequest());

    /// <summary>Waits (bounded) until the pipeline finished with the request.</summary>
    public async Task<RecordedRequest> CompletedAsync(string callId)
    {
        var record = For(callId);
        await record.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return record;
    }

    internal sealed class Filter(RequestRecorder recorder) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var callId = context.Request.Headers[CallHeader].ToString();
                if (callId.Length == 0)
                {
                    await nextMiddleware(context);
                    return;
                }

                var record = recorder.For(callId);
                record.TraceIdentifier = context.TraceIdentifier;
                var original = context.Response.Body;
                var tee = new TeeStream(original);
                context.Response.Body = tee;
                try
                {
                    await nextMiddleware(context);
                }
                catch (Exception ex)
                {
                    record.Escaped = ex;
                    throw;
                }
                finally
                {
                    record.StatusCode = context.Response.StatusCode;
                    record.HasStarted = context.Response.HasStarted;
                    record.ErrorIdHeader = context.Response.Headers.ContainsKey(ErrorContract.HeaderName);
                    record.Aborted = context.RequestAborted.IsCancellationRequested;
                    record.Body = tee.Text;
                    context.Response.Body = original;
                    record.Completed.TrySetResult();
                }
            });
            next(app);
        };
    }

    private sealed class TeeStream(Stream inner) : Stream
    {
        private readonly MemoryStream _copy = new();

        public string Text => Encoding.UTF8.GetString(_copy.ToArray());

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _copy.Write(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _copy.Write(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

/// <summary>An exception whose own <c>Message</c> and <c>ToString</c> throw, as a buggy library or a hostile type could.</summary>
internal sealed class HostileException : Exception
{
    public override string Message => throw new InvalidOperationException("Message of the hostile exception exploded.");

    public override string ToString() => throw new InvalidOperationException("ToString of the hostile exception exploded.");
}

/// <summary>
/// Makes a real request fail on purpose, from inside the use case, the way an infrastructure fault would. The real services stay
/// registered; a request that carries <see cref="FaultHeader"/> is made to fail where it calls them, every other request is untouched.
/// Each fault is raised from its own method, so different faults have different top application frames (and fingerprints).
/// </summary>
internal static class FaultInjection
{
    public const string FaultHeader = "X-Test-Fault";
    public const string MessageHeader = "X-Test-Fault-Message";

    public const string InvalidOperation = "invalid-operation";
    public const string WithInnerException = "inner";
    public const string OtherSite = "other-site";
    public const string ThirdSite = "third-site";
    public const string CanceledNotAborted = "canceled-not-aborted";
    public const string WaitForAbort = "wait-for-abort";
    public const string ResponseStarted = "response-started";
    public const string BrokenContext = "broken-context";
    public const string BadRequest400 = "bad-request-400";
    public const string BadRequest408 = "bad-request-408";
    public const string BadRequest413 = "bad-request-413";
    public const string HostileException = "hostile-exception";

    public static void Install(IServiceCollection services, AbortSignals signals)
    {
        services.AddSingleton(signals);
        Replace<IAuthService>(services, sp => new FaultyAuthService(
            ActivatorUtilities.CreateInstance<AuthService>(sp), sp.GetRequiredService<IHttpContextAccessor>(), signals));
        Replace<IProductService>(services, sp => new FaultyProductService(
            ActivatorUtilities.CreateInstance<ProductService>(sp), sp.GetRequiredService<IHttpContextAccessor>(), signals));
    }

    private static void Replace<TService>(IServiceCollection services, Func<IServiceProvider, TService> factory) where TService : class
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(TService)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddScoped(factory);
    }

    private static async Task MaybeFailAsync(IHttpContextAccessor accessor, AbortSignals signals, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext;
        var fault = context?.Request.Headers[FaultHeader].ToString();
        if (context is null || string.IsNullOrEmpty(fault))
        {
            return;
        }

        var message = context.Request.Headers[MessageHeader].ToString();
        switch (fault)
        {
            case InvalidOperation:
                RaiseInvalidOperation(message);
                break;
            case WithInnerException:
                RaiseWithInnerException(message);
                break;
            case OtherSite:
                RaiseNotSupported(message);
                break;
            case ThirdSite:
                RaiseTimeout(message);
                break;
            case CanceledNotAborted:
                RaiseCanceledWithoutAbort();
                break;
            case WaitForAbort:
                signals.MarkStarted(context.Request.Headers[RequestRecorder.CallHeader].ToString());
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case ResponseStarted:
                await context.Response.WriteAsync("partial-body");
                await context.Response.Body.FlushAsync();
                RaiseInvalidOperation(message);
                break;
            case BrokenContext:
                await BreakTheRequestContextAsync(context, cancellationToken);
                break;
            case BadRequest400:
                throw new BadHttpRequestException("Malformed request.", StatusCodes.Status400BadRequest);
            case BadRequest408:
                throw new BadHttpRequestException("Request timed out.", StatusCodes.Status408RequestTimeout);
            case BadRequest413:
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            case HostileException:
                RaiseHostileException();
                break;
        }
    }

    /// <summary>Leaves the request's own DbContext holding a row the database refuses (unknown tenant), as a failed write would.</summary>
    private static async Task BreakTheRequestContextAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
        db.Users.Add(Conversia.Domain.Entities.User.Create(Guid.NewGuid(), TestHelpers.UniqueEmail("orphan"), "not-a-real-hash", isOwner: false));
        await db.SaveChangesAsync(cancellationToken);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseInvalidOperation(string message) => throw new InvalidOperationException(message);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseWithInnerException(string message)
        => throw new InvalidOperationException(message, new ArgumentException($"inner cause of {message}", new FormatException("innermost cause")));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseHostileException() => throw new HostileException();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseNotSupported(string message) => throw new NotSupportedException(message);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseTimeout(string message) => throw new TimeoutException(message);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseCanceledWithoutAbort() => throw new OperationCanceledException("an internal operation was canceled");

    private sealed class FaultyAuthService(IAuthService inner, IHttpContextAccessor accessor, AbortSignals signals) : IAuthService
    {
        public async Task<AuthResponse> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.RegisterTenantAsync(request, cancellationToken);
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.LoginAsync(request, cancellationToken);
        }
    }

    private sealed class FaultyProductService(IProductService inner, IHttpContextAccessor accessor, AbortSignals signals) : IProductService
    {
        public async Task<PagedResponse<ProductResponse>> ListAsync(ProductQuery query, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.ListAsync(query, cancellationToken);
        }

        public async Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.GetAsync(id, cancellationToken);
        }

        public async Task<ProductResponse> CreateAsync(Guid callerTenantId, CreateProductRequest request, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.CreateAsync(callerTenantId, request, cancellationToken);
        }

        public async Task<ProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            return await inner.UpdateAsync(id, request, cancellationToken);
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await MaybeFailAsync(accessor, signals, cancellationToken);
            await inner.DeleteAsync(id, cancellationToken);
        }
    }
}

/// <summary>
/// Sits between EF Core and PostgreSQL for the commands whose text contains <see cref="Match"/> (and records all command texts):
/// <see cref="Before"/> can delay, gate or fail them.
/// </summary>
internal sealed class CommandInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public string Match { get; init; } = "ErrorLogs";

    /// <summary>Runs before a matching command; may await (delay, gate) or throw.</summary>
    public Func<string, CancellationToken, Task>? Before { get; set; }

    public IReadOnlyList<string> Commands => _commands.ToArray();

    /// <summary>
    /// The commands that are not the retention service's <c>DELETE</c> on <c>ErrorLogs</c>. That purge runs on its own at host startup
    /// and may land in the middle of a measurement, so tests that count what one registration cost count these (an INSERT is an INSERT
    /// whenever the purge happens, and any extra read would still show up) instead of waiting for the purge to be over.
    /// </summary>
    public IReadOnlyList<string> CommandsExceptRetentionPurge => _commands.Where(c => !IsRetentionPurge(c)).ToArray();

    public static bool IsRetentionPurge(string commandText)
        => commandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) && commandText.Contains("ErrorLogs", StringComparison.Ordinal);

    private readonly AsyncLocal<bool> _tracking = new();
    private readonly ConcurrentQueue<string> _tracked = new();

    /// <summary>
    /// From here on, commands sent by the code this test awaits (and only those: not a hosted service running on its own) are
    /// also recorded apart, so a test can count what one call cost even while background work shares the database.
    /// </summary>
    public IDisposable TrackCurrentFlow()
    {
        _tracking.Value = true;
        return new Stop(() => _tracking.Value = false);
    }

    public int TrackedCount(string verb)
        => _tracked.Count(c => c.Contains(verb, StringComparison.OrdinalIgnoreCase) && c.Contains(Match, StringComparison.OrdinalIgnoreCase));

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    public int CountOf(string verb) => Commands.Count(c => c.Contains(verb, StringComparison.OrdinalIgnoreCase) && c.Contains(Match, StringComparison.OrdinalIgnoreCase));

    private Task Intercept(DbCommand command, CancellationToken cancellationToken)
    {
        var text = command.CommandText;
        _commands.Enqueue(text);
        if (_tracking.Value)
        {
            _tracked.Enqueue(text);
        }

        return text.Contains(Match, StringComparison.OrdinalIgnoreCase) && Before is { } before
            ? before(text, cancellationToken)
            : Task.CompletedTask;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await Intercept(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await Intercept(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await Intercept(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Intercept(command, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Intercept(command, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Intercept(command, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }
}
