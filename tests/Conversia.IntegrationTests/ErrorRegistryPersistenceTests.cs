using System.Diagnostics;
using System.Net;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>IErrorRegistry</c> against the real database: what is stored, how codes are chosen and collisions resolved, and the promise
/// that it never throws and never hides the original error, whatever happens to the database.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorRegistryPersistenceTests
{
    private const string Secret = "S3cr3t-Value-42";

    private readonly CustomWebApplicationFactory _shared;
    private readonly ErrorTestHost _host;

    public ErrorRegistryPersistenceTests(CustomWebApplicationFactory shared)
    {
        _shared = shared;
        _host = ErrorTestHost.Default(shared);
    }

    private static ErrorReport Report(string marker, Action<ErrorReportBuilder>? adjust = null)
    {
        var builder = new ErrorReportBuilder
        {
            Path = $"/registry/{marker}",
            UserAgent = marker,
            ExceptionType = "Contoso.Registry.BoomException",
            ExceptionMessage = $"registry message {marker}",
            ExceptionDetails = $"Contoso.Registry.BoomException: registry message {marker}\n   at Conversia.Application.Services.OrganizationService.UpdateAsync(Guid id) in /src/OrganizationService.cs:line 42",
        };
        adjust?.Invoke(builder);
        return builder.Build();
    }

    // ---- what is stored ----

    [Fact]
    public async Task Registering_a_report_stores_its_fields_and_returns_the_code_it_stored_under()
    {
        var marker = ErrorTestHost.NewMarker();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow.AddSeconds(-30);
        var report = Report(marker, b =>
        {
            b.OccurredAt = occurredAt;
            b.Source = ErrorSource.Api;
            b.Kind = ErrorKind.UpstreamBadResponse;
            b.Environment = "Staging";
            b.ServiceVersion = "2.5.1";
            b.Instance = "api-pod-7";
            b.HttpMethod = "PATCH";
            b.Query = "?page=2";
            b.Endpoint = "Conversia.API.Controllers.BranchesController.UpdateBranch (PUT)";
            b.RouteValues = "{\"id\":\"3f2a\"}";
            b.StatusCode = 502;
            b.TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
            b.RequestId = "0HN123:00000001";
            b.UserId = userId;
            b.TenantId = tenantId;
            b.Context = "{\"upstream\":\"GET /api/branches\"}";
        });

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(report));

        Assert.Matches(ErrorContract.CodePattern, registration.Code);
        Assert.True(registration.Persisted);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code);
        Assert.Equal((int)ErrorSource.Api, row.Source);
        Assert.Equal((int)ErrorKind.UpstreamBadResponse, row.Kind);
        Assert.Equal(occurredAt, row.OccurredAt, TimeSpan.FromMilliseconds(5));
        Assert.Equal("Staging", row.Environment);
        Assert.Equal("2.5.1", row.ServiceVersion);
        Assert.Equal("api-pod-7", row.Instance);
        Assert.Equal("PATCH", row.HttpMethod);
        Assert.Equal(report.Path, row.Path);
        Assert.Equal("?page=2", row.Query);
        Assert.Equal(report.Endpoint, row.Endpoint);
        Assert.Equal(report.RouteValues, row.RouteValues);
        Assert.Equal(502, row.StatusCode);
        Assert.Equal(report.TraceId, row.TraceId);
        Assert.Equal(report.RequestId, row.RequestId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(marker, row.UserAgent);
        Assert.Equal(report.ExceptionType, row.ExceptionType);
        Assert.Equal(report.ExceptionMessage, row.ExceptionMessage);
        Assert.Equal(report.ExceptionDetails, row.ExceptionDetails);
        Assert.Equal(report.Context, row.Context);
        Assert.NotEqual(default, row.CreatedAt);
    }

    [Fact]
    public async Task Everything_stored_is_sanitized_whatever_field_it_arrives_in()
    {
        var marker = ErrorTestHost.NewMarker();
        var report = Report(marker, b =>
        {
            b.Query = $"?page=2&token={Secret}q&api_key={Secret}k";
            b.ExceptionMessage = $"Falló Server=db;User Id=svc_registry;Password={Secret}m; fin";
            b.ExceptionDetails = $"System.Exception: Falló con Authorization: Bearer {Secret}d y password={Secret}p\n   at Conversia.A.B()";
            b.Context = $"{{\"authorization\":\"Bearer {Secret}c\",\"upstream\":\"GET /api/branches\"}}";
            b.UserAgent = $"{marker} token={Secret}u";
            b.RouteValues = $"{{\"id\":\"1\",\"token\":\"{Secret}r\"}}";
            b.Endpoint = $"/conversaciones?secret={Secret}e";
        });

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(report));

        Assert.True(registration.Persisted);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code);
        row.AssertNowhereInRow(Secret, "svc_registry");
        Assert.Contains("page=2", row.Query);
        Assert.Contains("Falló", row.ExceptionMessage);
        Assert.Contains("fin", row.ExceptionMessage);
        Assert.Contains("upstream", row.Context);
        Assert.StartsWith(marker, row.UserAgent);
        Assert.Contains("Conversia.A.B", row.ExceptionDetails);
    }

    public static TheoryData<string, int> Limits
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var (column, limit) in ErrorContract.StoredLimits)
            {
                data.Add(column, limit);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task An_oversized_value_is_cut_to_its_column_and_the_registration_still_succeeds(string field, int limit)
    {
        var marker = ErrorTestHost.NewMarker();
        var oversized = new string('x', limit * 3 + 7);
        var report = Report(marker, b => b.Set(field, oversized));

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(report));

        Assert.True(registration.Persisted, $"{field} of {oversized.Length} characters was not stored.");
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code);
        var stored = (string?)typeof(ErrorLogRow).GetProperty(field)!.GetValue(row);
        Assert.NotNull(stored);
        Assert.True(stored!.Length <= limit, $"{field}: {stored.Length} > {limit}");
        Assert.StartsWith(new string('x', Math.Max(1, Math.Min(limit, 40) - 11)), stored); // the front of the value is what is kept
        if (limit >= 50)
        {
            Assert.EndsWith(Conversia.Application.ErrorLogging.ErrorSanitizer.TruncationMarker, stored);
        }
    }

    [Fact]
    public async Task A_report_with_only_the_required_values_is_stored_and_gets_a_fingerprint()
    {
        var report = new ErrorReport
        {
            OccurredAt = DateTime.UtcNow,
            Source = ErrorSource.Web,
            Kind = ErrorKind.Unhandled,
            Path = $"/minimal/{Guid.NewGuid():N}",
            ExceptionType = "System.Exception",
        };

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(report));

        Assert.True(registration.Persisted);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code);
        Assert.Equal((int)ErrorSource.Web, row.Source);
        Assert.Null(row.UserId);
        Assert.Null(row.TenantId);
        Assert.Matches(ErrorContract.FingerprintPattern, row.Fingerprint!);
    }

    [Fact]
    public async Task The_fingerprint_is_computed_when_absent_and_kept_when_supplied()
    {
        var marker = ErrorTestHost.NewMarker();
        var computed = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b =>
        {
            b.ExceptionType = "System.Net.Http.HttpRequestException";
            b.ExceptionMessage = "una cosa";
            b.ExceptionDetails = ReportApi.WebStack;
        })));
        var supplied = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => b.Fingerprint = "0123456789abcdef")));

        var computedRow = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, computed.Code);
        var suppliedRow = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, supplied.Code);
        Assert.Equal(ErrorContract.ExpectedFingerprint("System.Net.Http.HttpRequestException", ReportApi.WebTopFrame), computedRow.Fingerprint);
        Assert.Equal("0123456789abcdef", suppliedRow.Fingerprint);
    }

    [Fact]
    public async Task One_registration_is_one_insert_and_nothing_else()
    {
        // A host of its own on its own empty database, measured after its startup purge has finished: nothing else is talking to it.
        await using var own = await ErrorTestHost.StartOnOwnDatabaseAsync(_shared);
        var host = own.Host;
        var marker = ErrorTestHost.NewMarker();
        await host.WithRegistryAsync(r => r.RegisterAsync(Report(marker + "-warm"))); // cold start
        var before = host.Commands.CommandsExceptRetentionPurge.Count;

        var registration = await host.WithRegistryAsync(r => r.RegisterAsync(Report(marker)));
        var during = host.Commands.CommandsExceptRetentionPurge.Skip(before).ToList();

        Assert.True(registration.Persisted);
        var command = Assert.Single(during);
        Assert.Contains("INSERT", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ErrorLogs", command);
    }

    [Fact]
    public async Task A_caller_that_already_gave_up_does_not_make_the_registry_throw()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var failure = await Record.ExceptionAsync(async () =>
        {
            var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(ErrorTestHost.NewMarker()), cancelled.Token));
            Assert.Matches(ErrorContract.CodePattern, registration.Code);
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task Odd_reports_are_handled_without_throwing_and_still_get_a_code()
    {
        var reports = new[]
        {
            new ErrorReport(),
            new ErrorReport { ExceptionType = "", Path = "", OccurredAt = default },
            new ErrorReport { OccurredAt = DateTime.MinValue, Path = "/\0/\u0007/control", ExceptionType = "X", ExceptionMessage = "\0\0\0" },
            new ErrorReport { OccurredAt = DateTime.MaxValue, Path = "/emoji/😀😀", ExceptionType = "X", ExceptionMessage = "😀" },
            new ErrorReport { OccurredAt = DateTime.UtcNow, Path = "/sql", ExceptionType = "X'; DROP TABLE ErrorLogs;--", ExceptionMessage = "'; DELETE FROM Users;--", Source = (ErrorSource)99, Kind = (ErrorKind)99 },
        };

        foreach (var report in reports)
        {
            ErrorRegistration? registration = null;
            var failure = await Record.ExceptionAsync(async () => registration = await _host.WithRegistryAsync(r => r.RegisterAsync(report)));

            Assert.Null(failure);
            Assert.Matches(ErrorContract.CodePattern, registration!.Code);
        }

        Assert.True(await ErrorLogDb.CountAsync(_host.ConnectionString) > 0); // the table is still there
    }

    // ---- codes ----

    [Fact]
    public async Task A_valid_unused_code_supplied_by_the_reporter_is_honored()
    {
        var code = ErrorContract.NewCode();
        var marker = ErrorTestHost.NewMarker();

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => b.Code = code)));

        Assert.Equal(code, registration.Code);
        Assert.True(registration.Persisted);
        Assert.Equal(marker, (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code)).UserAgent);
    }

    public static TheoryData<string> InvalidClientCodes => new()
    {
        "ERR-1", "err-20261018-abcdef", "ERR-20261018-ABCDEI", "ERR-20261018-ABCDE", "'; DROP TABLE ErrorLogs;--", "   ",
        "ERR-20261018-ABCDEF\n", "not a code", "ERR-20261018-" + "A", "E",
    };

    [Theory]
    [MemberData(nameof(InvalidClientCodes))]
    public async Task An_invalid_code_from_the_reporter_is_replaced_by_a_valid_one(string given)
    {
        var marker = ErrorTestHost.NewMarker();

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => b.Code = given)));

        Assert.NotEqual(given, registration.Code);
        Assert.Matches(ErrorContract.CodePattern, registration.Code);
        Assert.True(registration.Persisted);
        Assert.Equal(marker, (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code)).UserAgent);
        Assert.Null(await ErrorLogDb.FindByCodeAsync(_host.ConnectionString, given));
    }

    [Fact]
    public async Task An_oversized_code_from_the_reporter_is_replaced_not_truncated()
    {
        var given = ErrorContract.NewCode() + new string('Z', 400);

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(ErrorTestHost.NewMarker(), b => b.Code = given)));

        Assert.Matches(ErrorContract.CodePattern, registration.Code);
        Assert.True(registration.Persisted);
        Assert.NotEqual(given[..19], given);
        Assert.Null(await ErrorLogDb.FindByCodeAsync(_host.ConnectionString, given[..19]));
    }

    [Fact]
    public async Task A_code_that_is_already_used_is_not_reused_and_the_existing_row_is_left_alone()
    {
        var taken = ErrorContract.NewCode();
        await ErrorLogDb.InsertAsync(_host.ConnectionString, new SeedErrorRow(taken, DateTime.UtcNow, Path: "/planted-first"));
        var marker = ErrorTestHost.NewMarker();

        var registration = await _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => b.Code = taken)));

        Assert.NotEqual(taken, registration.Code);
        Assert.Matches(ErrorContract.CodePattern, registration.Code);
        Assert.True(registration.Persisted);
        var newRow = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, registration.Code);
        Assert.Equal(marker, newRow.UserAgent);
        var original = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, taken);
        Assert.Equal("/planted-first", original.Path);
        Assert.Equal("seeded message", original.ExceptionMessage);
    }

    [Fact]
    public async Task Ten_parallel_reports_asking_for_the_same_code_give_it_to_exactly_one_and_store_all_ten()
    {
        var contested = ErrorContract.NewCode();
        var marker = ErrorTestHost.NewMarker();

        var registrations = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            _host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => { b.Code = contested; b.ExceptionMessage = $"racer {i}"; })))));

        Assert.Equal(1, registrations.Count(r => r.Code == contested));
        Assert.Equal(10, registrations.Select(r => r.Code).Distinct().Count());
        Assert.All(registrations, r => Assert.Matches(ErrorContract.CodePattern, r.Code));
        Assert.All(registrations, r => Assert.True(r.Persisted));
        var rows = await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker);
        Assert.Equal(10, rows.Count);
        Assert.Equal(registrations.Select(r => r.Code).Order(), rows.Select(r => r.Code).Order());
        Assert.Equal(10, rows.Select(r => r.ExceptionMessage).Distinct().Count()); // each racer's own report, none overwritten
    }

    // ---- ErrorLogging:Enabled = false ----

    [Fact]
    public async Task With_logging_disabled_the_code_is_still_generated_returned_and_logged_but_nothing_is_stored()
    {
        var disabled = _shared.GetOrAddHost("error-registry:disabled", () => ErrorTestHost.Create(
            _shared, ErrorTestHost.Settings(("ErrorLogging:Enabled", "false"))));
        var marker = ErrorTestHost.NewMarker();
        using var disabledClient = disabled.NewClient();
        using var enabledClient = _host.NewClient();

        using var offResponse = await disabledClient.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-off"));
        using var onResponse = await enabledClient.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-on"));

        var offCode = await ErrorContract.AssertUnexpectedAsync(offResponse);
        var onCode = await ErrorContract.AssertUnexpectedAsync(onResponse);
        Assert.NotEqual(offCode, onCode);
        Assert.Contains(disabled.Logs.Mentioning(offCode), e => e.Level >= LogLevel.Warning);
        var rows = await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker);
        Assert.Equal(onCode, Assert.Single(rows).Code); // the enabled host stored its failure, the disabled one did not
        Assert.Null(await ErrorLogDb.FindByCodeAsync(_host.ConnectionString, offCode));

        var before = disabled.Commands.CommandsExceptRetentionPurge.Count;
        var direct = await disabled.WithRegistryAsync(r => r.RegisterAsync(Report(marker)));
        Assert.False(direct.Persisted);
        Assert.Matches(ErrorContract.CodePattern, direct.Code);
        Assert.Equal(before, disabled.Commands.CommandsExceptRetentionPurge.Count);
        Assert.Null(await ErrorLogDb.FindByCodeAsync(_host.ConnectionString, direct.Code));
    }

    // ---- the database is gone or slow: the request still completes, the error is not lost ----

    public static TheoryData<string> UnavailableDatabases => new() { "unreachable-server", "database-that-does-not-exist" };

    private string UnavailableConnectionString(string kind) => kind == "unreachable-server"
        ? TestDatabase.UnreachableConnectionString
        : new NpgsqlConnectionStringBuilder(_shared.ServerConnectionString) { Database = $"dropped_{Guid.NewGuid():N}", Timeout = 5, Pooling = false }.ConnectionString;

    [Theory]
    [MemberData(nameof(UnavailableDatabases))]
    public async Task When_the_database_is_unavailable_the_registry_does_not_throw_reports_it_and_logs_what_it_could_not_store(string kind)
    {
        var host = ErrorTestHost.Create(
            _shared, ErrorTestHost.Settings(("ErrorLogging:PersistTimeoutSeconds", "2")), UnavailableConnectionString(kind));
        try
        {
            var marker = ErrorTestHost.NewMarker();
            var fingerprint = $"{Guid.NewGuid():N}"[..16];
            var path = $"/lost/{Guid.NewGuid():N}";
            var stopwatch = Stopwatch.StartNew();

            var registration = await host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b =>
            {
                b.Path = path;
                b.Fingerprint = fingerprint;
                b.ExceptionType = "Contoso.Lost.LostException";
                b.ExceptionMessage = "the message that must survive in the log";
            })));
            stopwatch.Stop();

            Assert.False(registration.Persisted);
            Assert.Matches(ErrorContract.CodePattern, registration.Code);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2 + 10), $"Took {stopwatch.Elapsed}");
            ErrorContract.AssertLoggedWithKeyFields(
                host.Logs, registration.Code, path, "Contoso.Lost.LostException", "the message that must survive in the log", fingerprint, "Api");
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(UnavailableDatabases))]
    public async Task When_the_database_is_unavailable_the_request_still_ends_in_the_normal_500_with_a_code_and_the_original_error_is_logged(string kind)
    {
        var host = ErrorTestHost.Create(
            _shared, ErrorTestHost.Settings(("ErrorLogging:PersistTimeoutSeconds", "2")), UnavailableConnectionString(kind));
        try
        {
            var marker = ErrorTestHost.NewMarker();
            using var client = host.NewClient();
            var stopwatch = Stopwatch.StartNew();

            using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: $"original failure {marker}"));
            stopwatch.Stop();

            var code = await ErrorContract.AssertUnexpectedAsync(response);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2 + 10), $"Took {stopwatch.Elapsed}");
            Assert.Contains(host.Logs.Mentioning(code), e => e.Level == LogLevel.Error && e.Mentions($"original failure {marker}") && e.Mentions("InvalidOperationException"));

            // The host keeps working: another failure, and an ordinary request.
            using var second = await client.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-2"));
            Assert.NotEqual(code, await ErrorContract.AssertUnexpectedAsync(second));
            using var malformed = await client.PostAsync("/api/auth/login", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Fact]
    public async Task A_database_slower_than_the_persist_timeout_does_not_hold_the_request_up()
    {
        var slow = new CommandInterceptor
        {
            Match = "ErrorLogs",
            Before = async (text, token) =>
            {
                if (text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), token);
                }
            },
        };
        var host = ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(("ErrorLogging:PersistTimeoutSeconds", "1")),
            configureServices: (services, _) => ErrorTestHost.ReplaceDatabase(services, _shared.ServerConnectionString, slow));
        try
        {
            var marker = ErrorTestHost.NewMarker();
            using var client = host.NewClient();

            var direct = Stopwatch.StartNew();
            var fingerprint = $"{Guid.NewGuid():N}"[..16];
            var registration = await host.WithRegistryAsync(r => r.RegisterAsync(Report(marker, b => b.Fingerprint = fingerprint)));
            direct.Stop();

            Assert.False(registration.Persisted);
            Assert.Matches(ErrorContract.CodePattern, registration.Code);
            Assert.True(slow.CountOf("INSERT") >= 1, "The slow insert was never attempted, so the timeout was not exercised.");
            Assert.True(direct.Elapsed < TimeSpan.FromSeconds(1 + 8), $"Took {direct.Elapsed}");
            ErrorContract.AssertLoggedWithKeyFields(
                host.Logs, registration.Code, $"/registry/{marker}", "Contoso.Registry.BoomException", $"registry message {marker}", fingerprint);

            var http = Stopwatch.StartNew();
            using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: $"slow database failure {marker}"));
            http.Stop();

            var code = await ErrorContract.AssertUnexpectedAsync(response);
            Assert.True(http.Elapsed < TimeSpan.FromSeconds(1 + 8), $"Took {http.Elapsed}");
            Assert.Contains(host.Logs.Mentioning(code), e => e.Level == LogLevel.Error && e.Mentions($"slow database failure {marker}"));
        }
        finally
        {
            host.Factory.Dispose();
        }
    }
}

/// <summary>Mutable builder for the init-only <see cref="ErrorReport"/>, so a test can set a field by its name.</summary>
internal sealed class ErrorReportBuilder
{
    public string? Code { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public ErrorSource Source { get; set; } = ErrorSource.Api;

    public ErrorKind Kind { get; set; } = ErrorKind.Unhandled;

    public string? Environment { get; set; }

    public string? ServiceVersion { get; set; }

    public string? Instance { get; set; }

    public string? HttpMethod { get; set; }

    public string? Path { get; set; }

    public string? Query { get; set; }

    public string? Endpoint { get; set; }

    public string? RouteValues { get; set; }

    public int StatusCode { get; set; } = 500;

    public string? TraceId { get; set; }

    public string? RequestId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? TenantId { get; set; }

    public string? UserAgent { get; set; }

    public string ExceptionType { get; set; } = "System.Exception";

    public string? ExceptionMessage { get; set; }

    public string? ExceptionDetails { get; set; }

    public string? Fingerprint { get; set; }

    public string? Context { get; set; }

    public void Set(string field, string value) => GetType().GetProperty(field)!.SetValue(this, value);

    public ErrorReport Build() => new()
    {
        Code = Code,
        OccurredAt = OccurredAt,
        Source = Source,
        Kind = Kind,
        Environment = Environment,
        ServiceVersion = ServiceVersion,
        Instance = Instance,
        HttpMethod = HttpMethod,
        Path = Path,
        Query = Query,
        Endpoint = Endpoint,
        RouteValues = RouteValues,
        StatusCode = StatusCode,
        TraceId = TraceId,
        RequestId = RequestId,
        UserId = UserId,
        TenantId = TenantId,
        UserAgent = UserAgent,
        ExceptionType = ExceptionType,
        ExceptionMessage = ExceptionMessage,
        ExceptionDetails = ExceptionDetails,
        Fingerprint = Fingerprint,
        Context = Context,
    };
}
