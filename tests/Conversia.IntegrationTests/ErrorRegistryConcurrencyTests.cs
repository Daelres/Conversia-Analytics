using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Many users failing at once: every failure gets its own code and its own row (and the right user, tenant and path), and the
/// storm control lets only <c>MaxPerFingerprintPerMinute</c> rows per defect through while every occurrence still gets a code and a log entry.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorRegistryConcurrencyTests
{
    private readonly CustomWebApplicationFactory _shared;
    private readonly ErrorTestHost _main;

    public ErrorRegistryConcurrencyTests(CustomWebApplicationFactory shared)
    {
        _shared = shared;
        _main = ErrorTestHost.Default(shared);
    }

    private ErrorTestHost Storm(int budget = 3)
        => _shared.GetOrAddHost($"error-registry:storm-{budget}", () => ErrorTestHost.Create(
            _shared, ErrorTestHost.Settings(("ErrorLogging:MaxPerFingerprintPerMinute", budget.ToString()))));

    [Fact]
    public async Task Fifty_parallel_failures_get_fifty_distinct_codes_and_exactly_fifty_rows()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _main.NewClient();

        var codes = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: $"parallel failure {i}", callId: $"{marker}-{i}"));
            return await ErrorContract.AssertUnexpectedAsync(response);
        }));

        Assert.Equal(50, codes.Distinct().Count());
        var rows = await ErrorLogDb.ByUserAgentAsync(_main.ConnectionString, marker);
        Assert.Equal(50, rows.Count);
        Assert.Equal(codes.Order(), rows.Select(r => r.Code).Order());
        Assert.Equal(50, rows.Select(r => r.ExceptionMessage).Distinct().Count()); // each row kept its own request's message
    }

    [Fact]
    public async Task Parallel_failures_of_different_tenants_never_mix_their_user_tenant_or_path()
    {
        using var anonymous = _main.NewClient();
        var tenants = new List<TenantSession>();
        for (var i = 0; i < 8; i++)
        {
            tenants.Add(await RegisterTenantSessionAsync(anonymous, $"parallel{i}"));
        }

        var marker = ErrorTestHost.NewMarker();
        var calls = tenants
            .SelectMany(tenant => Enumerable.Range(0, 6).Select(n => (Tenant: tenant, ProductId: Guid.NewGuid(), Call: $"{marker}-{tenant.TenantId:N}-{n}")))
            .ToList();

        var results = await Task.WhenAll(calls.Select(async call =>
        {
            using var client = _main.NewClient();
            using var response = await client.SendAsync(ErrorTestHost.FailingProductUpdate(call.ProductId, marker, call.Tenant.OwnerToken, callId: call.Call));
            return (call, Code: await ErrorContract.AssertUnexpectedAsync(response));
        }));

        Assert.Equal(48, results.Select(r => r.Code).Distinct().Count());
        var rows = (await ErrorLogDb.ByUserAgentAsync(_main.ConnectionString, marker)).ToDictionary(r => r.Code);
        Assert.Equal(48, rows.Count);
        foreach (var (call, code) in results)
        {
            var row = rows[code];
            Assert.Equal(call.Tenant.TenantId, row.TenantId);
            Assert.Equal(call.Tenant.OwnerId, row.UserId);
            Assert.Equal($"/api/products/{call.ProductId}", row.Path);
        }
    }

    [Fact]
    public async Task With_a_budget_of_three_twenty_parallel_failures_get_twenty_codes_but_only_three_rows()
    {
        var storm = Storm();
        var marker = ErrorTestHost.NewMarker();
        using var client = storm.NewClient();

        var codes = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.InvalidOperation, $"storm {i}", $"{marker}-{i}"));
            return await ErrorContract.AssertUnexpectedAsync(response);
        }));

        Assert.Equal(20, codes.Distinct().Count());
        var rows = await ErrorLogDb.ByUserAgentAsync(storm.ConnectionString, marker);
        Assert.Equal(3, rows.Count);
        Assert.Subset(codes.ToHashSet(), rows.Select(r => r.Code).ToHashSet());

        var notStored = codes.Except(rows.Select(r => r.Code)).ToList();
        Assert.Equal(17, notStored.Count);
        foreach (var code in notStored)
        {
            Assert.Contains(storm.Logs.Mentioning(code), e => e.Level == LogLevel.Error);
        }

        // A different defect has its own budget, even in the middle of a storm.
        using var other = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.OtherSite, "another defect", marker + "-other"));
        var otherCode = await ErrorContract.AssertUnexpectedAsync(other);
        var otherRow = await ErrorLogDb.RequireByCodeAsync(storm.ConnectionString, otherCode);
        Assert.NotEqual(rows[0].Fingerprint, otherRow.Fingerprint);
    }

    [Fact]
    public async Task Registrations_that_storm_control_refuses_are_logged_with_the_key_fields_and_cost_no_database_command()
    {
        var storm = Storm(2);
        var fingerprint = $"{Guid.NewGuid():N}"[..16];
        const string exceptionType = "Contoso.Storm.BoomException";
        var path = $"/storm/{Guid.NewGuid():N}";
        var results = new List<Conversia.Application.DTOs.Diagnostics.ErrorRegistration>();

        for (var i = 0; i < 5; i++)
        {
            results.Add(await storm.WithRegistryAsync(registry => registry.RegisterAsync(new Conversia.Application.DTOs.Diagnostics.ErrorReport
            {
                OccurredAt = DateTime.UtcNow,
                Source = Conversia.Domain.Enums.ErrorSource.Api,
                Kind = Conversia.Domain.Enums.ErrorKind.Unhandled,
                Path = path,
                HttpMethod = "GET",
                StatusCode = 500,
                ExceptionType = exceptionType,
                ExceptionMessage = $"storm message {i}",
                ExceptionDetails = $"{exceptionType}: storm message {i}",
                Fingerprint = fingerprint,
            })));
        }

        Assert.Equal(5, results.Select(r => r.Code).Distinct().Count());
        Assert.All(results, r => Assert.Matches(ErrorContract.CodePattern, r.Code));
        Assert.Equal(new[] { true, true, false, false, false }, results.Select(r => r.Persisted).ToArray());
        var stored = await ErrorLogDb.QueryAsync(storm.ConnectionString, "\"Fingerprint\" = @f", new Npgsql.NpgsqlParameter("@f", fingerprint));
        Assert.Equal(results.Take(2).Select(r => r.Code).Order(), stored.Select(r => r.Code).Order());

        foreach (var refused in results.Skip(2))
        {
            ErrorContract.AssertLoggedWithKeyFields(storm.Logs, refused.Code, path, exceptionType, fingerprint, "storm message");
        }

        // Once over budget a registration reads nothing and writes nothing.
        var before = storm.Commands.CommandsExceptRetentionPurge.Count;
        var again = await storm.WithRegistryAsync(registry => registry.RegisterAsync(new Conversia.Application.DTOs.Diagnostics.ErrorReport
        {
            OccurredAt = DateTime.UtcNow,
            Source = Conversia.Domain.Enums.ErrorSource.Api,
            Kind = Conversia.Domain.Enums.ErrorKind.Unhandled,
            Path = path,
            ExceptionType = exceptionType,
            Fingerprint = fingerprint,
        }));
        Assert.False(again.Persisted);
        Assert.Equal(before, storm.Commands.CommandsExceptRetentionPurge.Count);
    }
}
