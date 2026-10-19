using System.Net;
using System.Text;
using Npgsql;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.IntegrationTests;

/// <summary>
/// Retention: rows older than <c>ErrorLogging:RetentionDays</c> are purged at startup (without delaying readiness) and by
/// <c>IErrorLogPurger</c> in batches of 1 000; a failing purge is logged and never stops the host. Each test owns a throw-away
/// database, because purging is only observable on a table nobody else writes to.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorLogRetentionTests
{
    private readonly CustomWebApplicationFactory _shared;

    public ErrorLogRetentionTests(CustomWebApplicationFactory shared) => _shared = shared;

    private async Task<TestDatabase> MigratedDatabaseAsync()
    {
        var database = TestDatabase.Create(_shared);
        await database.MigrateAsync();
        return database;
    }

    private static async Task<List<string>> CodesAsync(string connectionString)
        => (await ErrorLogDb.QueryAsync(connectionString)).Select(r => r.Code).ToList();

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string description, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not true within {timeoutSeconds}s: {description}");
            await Task.Delay(50);
        }
    }

    /// <summary>A host on <paramref name="database"/> whose retention is long enough that its own startup purge has nothing to do.</summary>
    private ErrorTestHost QuietHost(TestDatabase database, params IInterceptor[] interceptors)
    {
        var host = ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(("ErrorLogging:RetentionDays", "3650")),
            database.ConnectionString,
            (services, _) => ErrorTestHost.ReplaceDatabase(services, database.ConnectionString, interceptors));
        _ = host.Factory.Services; // start it now, so that its startup work is not mistaken for the call a test is about to make
        return host;
    }

    // ---- the purger ----

    [Fact]
    public async Task The_purger_deletes_exactly_the_rows_older_than_the_cutoff_and_nothing_else()
    {
        await using var database = await MigratedDatabaseAsync();
        var host = QuietHost(database);
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-90);
            await ErrorLogDb.InsertAsync(database.ConnectionString, new SeedErrorRow("ERR-20200101-000001", cutoff.AddHours(-1)));
            await ErrorLogDb.InsertAsync(database.ConnectionString, new SeedErrorRow("ERR-20200101-000002", cutoff.AddSeconds(-1)));
            await ErrorLogDb.InsertAsync(database.ConnectionString, new SeedErrorRow("ERR-20200101-000003", cutoff.AddSeconds(1)));
            await ErrorLogDb.InsertAsync(database.ConnectionString, new SeedErrorRow("ERR-20200101-000004", cutoff.AddHours(1)));
            await ErrorLogDb.InsertAsync(database.ConnectionString, new SeedErrorRow("ERR-20200101-000005", DateTime.UtcNow));
            Assert.Equal(5, await ErrorLogDb.CountAsync(database.ConnectionString)); // precondition: the rows are there

            var deleted = await host.WithPurgerAsync(purger => purger.PurgeAsync(cutoff));

            Assert.Equal(2, deleted);
            Assert.Equal(
                new[] { "ERR-20200101-000003", "ERR-20200101-000004", "ERR-20200101-000005" },
                (await CodesAsync(database.ConnectionString)).Order().ToArray());
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Fact]
    public async Task Purging_twice_is_harmless_and_an_empty_table_or_a_far_past_cutoff_deletes_nothing()
    {
        await using var database = await MigratedDatabaseAsync();
        var host = QuietHost(database);
        try
        {
            Assert.Equal(0, await host.WithPurgerAsync(p => p.PurgeAsync(DateTime.UtcNow))); // empty table

            await ErrorLogDb.InsertManyAsync(database.ConnectionString, 10, DateTime.UtcNow.AddDays(-200), "20200101");
            Assert.Equal(10, await ErrorLogDb.CountAsync(database.ConnectionString));
            Assert.Equal(0, await host.WithPurgerAsync(p => p.PurgeAsync(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
            Assert.Equal(0, await host.WithPurgerAsync(p => p.PurgeAsync(DateTime.MinValue)));
            Assert.Equal(10, await ErrorLogDb.CountAsync(database.ConnectionString));

            var cutoff = DateTime.UtcNow.AddDays(-90);
            Assert.Equal(10, await host.WithPurgerAsync(p => p.PurgeAsync(cutoff)));
            Assert.Equal(0, await host.WithPurgerAsync(p => p.PurgeAsync(cutoff)));
            Assert.Equal(0, await ErrorLogDb.CountAsync(database.ConnectionString));
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2500, 3)]
    public async Task More_than_a_thousand_old_rows_are_purged_in_several_batches(int oldRows, int minimumDeletes)
    {
        await using var database = await MigratedDatabaseAsync();
        var counter = new CommandInterceptor { Match = "ErrorLogs" };
        var host = QuietHost(database, counter);
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-90);
            await ErrorLogDb.InsertManyAsync(database.ConnectionString, oldRows, cutoff.AddDays(-10), "20200101");
            await ErrorLogDb.InsertManyAsync(database.ConnectionString, 30, cutoff.AddDays(10), "20260101");
            Assert.Equal(oldRows + 30, await ErrorLogDb.CountAsync(database.ConnectionString));
            int deleted;
            int deleteCommands;
            using (counter.TrackCurrentFlow()) // the host's own startup purge runs elsewhere and must not count here
            {
                deleted = await host.WithPurgerAsync(p => p.PurgeAsync(cutoff));
                deleteCommands = counter.TrackedCount("DELETE");
            }

            Assert.Equal(oldRows, deleted);
            Assert.Equal(30, await ErrorLogDb.CountAsync(database.ConnectionString));
            Assert.Equal(30, await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20260101-%'"));
            Assert.True(deleteCommands >= minimumDeletes, $"{oldRows} rows were deleted with {deleteCommands} DELETE command(s); batches of 1 000 need at least {minimumDeletes}.");
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Fact]
    public async Task After_two_purges_at_once_every_old_row_is_gone_and_the_recent_ones_are_intact()
    {
        await using var database = await MigratedDatabaseAsync();
        var host = QuietHost(database);
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-90);
            await ErrorLogDb.InsertManyAsync(database.ConnectionString, 3000, cutoff.AddDays(-5), "20200101");
            await ErrorLogDb.InsertManyAsync(database.ConnectionString, 20, cutoff.AddDays(5), "20260101");

            // The requirement does not promise that concurrent purges are safe (two batched DELETEs can legitimately deadlock, SQLSTATE
            // 40P01), only that the final state is right; a call that lost a deadlock is retried once.
            await Task.WhenAll(PurgeRetryingOnDeadlockAsync(host, cutoff), PurgeRetryingOnDeadlockAsync(host, cutoff));

            Assert.Equal(0, await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20200101-%'"));
            Assert.Equal(20, await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20260101-%'"));
            Assert.Equal(20, await ErrorLogDb.CountAsync(database.ConnectionString));
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    private static async Task<int> PurgeRetryingOnDeadlockAsync(ErrorTestHost host, DateTime cutoff)
    {
        try
        {
            return await host.WithPurgerAsync(p => p.PurgeAsync(cutoff));
        }
        catch (Exception ex) when (IsDeadlockVictim(ex))
        {
            return await host.WithPurgerAsync(p => p.PurgeAsync(cutoff));
        }
    }

    private static bool IsDeadlockVictim(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task A_purge_that_is_cancelled_leaves_the_table_consistent()
    {
        await using var database = await MigratedDatabaseAsync();
        var host = QuietHost(database);
        try
        {
            await ErrorLogDb.InsertManyAsync(database.ConnectionString, 50, DateTime.UtcNow.AddDays(-200), "20200101");
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var failure = await Record.ExceptionAsync(() => host.WithPurgerAsync(p => p.PurgeAsync(DateTime.UtcNow, cancelled.Token)));

            Assert.True(failure is null or OperationCanceledException, $"Unexpected failure: {failure}");
            Assert.Equal(50, await ErrorLogDb.CountAsync(database.ConnectionString)); // nothing was deleted by a purge that never started
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    // ---- at startup ----

    [Theory]
    [InlineData("30", 31, 29)]
    [InlineData(null, 91, 89)]
    public async Task At_startup_old_rows_are_purged_in_the_background_without_delaying_the_host(string? retentionDays, int oldDays, int recentDays)
    {
        await using var database = await MigratedDatabaseAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var purgeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new CommandInterceptor
        {
            Match = "ErrorLogs",
            Before = async (text, token) =>
            {
                if (text.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    purgeStarted.TrySetResult();
                    await gate.Task.WaitAsync(token);
                }
            },
        };
        await ErrorLogDb.InsertManyAsync(database.ConnectionString, 7, DateTime.UtcNow.AddDays(-oldDays), "20200101");
        await ErrorLogDb.InsertManyAsync(database.ConnectionString, 5, DateTime.UtcNow.AddDays(-recentDays), "20260101");
        await ErrorLogDb.InsertManyAsync(database.ConnectionString, 3, DateTime.UtcNow, "20260102");
        Assert.Equal(15, await ErrorLogDb.CountAsync(database.ConnectionString));

        var settings = retentionDays is null ? new Dictionary<string, string>() : ErrorTestHost.Settings(("ErrorLogging:RetentionDays", retentionDays));
        var host = ErrorTestHost.Create(
            _shared, settings, database.ConnectionString,
            (services, _) => ErrorTestHost.ReplaceDatabase(services, database.ConnectionString, held));
        try
        {
            // Building the host runs the hosted services' StartAsync. If it waited for the purge, which is held, this would never finish.
            await Task.Run(() => host.Factory.Services).WaitAsync(TimeSpan.FromSeconds(60));
            await purgeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // and the purge really does run at startup
            Assert.Equal(15, await ErrorLogDb.CountAsync(database.ConnectionString)); // still held: nothing deleted yet

            gate.SetResult();

            await EventuallyAsync(
                async () => await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20200101-%'") == 0,
                "the old rows are purged after the startup purge is released");
            Assert.Equal(5, await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20260101-%'"));
            Assert.Equal(3, await ErrorLogDb.CountAsync(database.ConnectionString, "\"Code\" LIKE 'ERR-20260102-%'"));
        }
        finally
        {
            gate.TrySetResult();
            host.Factory.Dispose();
        }
    }

    public static TheoryData<string> FailingPurges => new() { "delete is refused", "database unreachable" };

    [Theory]
    [MemberData(nameof(FailingPurges))]
    public async Task A_purge_that_fails_at_startup_is_logged_and_never_stops_the_host(string failure)
    {
        await using var database = await MigratedDatabaseAsync();
        await ErrorLogDb.InsertManyAsync(database.ConnectionString, 4, DateTime.UtcNow.AddDays(-400), "20200101");
        var refuse = new CommandInterceptor
        {
            Match = "ErrorLogs",
            Before = (text, _) => text.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("the delete was refused by the test")
                : Task.CompletedTask,
        };
        var connectionString = failure == "database unreachable" ? TestDatabase.UnreachableConnectionString : database.ConnectionString;
        var host = ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(("ErrorLogging:RetentionDays", "30")),
            connectionString,
            (services, _) => ErrorTestHost.ReplaceDatabase(services, connectionString, refuse));
        try
        {
            var start = Record.Exception(() => host.Factory.Services);
            Assert.Null(start);

            var logged = await host.Logs.WaitForAsync(
                e => e.Level == LogLevel.Error && (e.Category.Contains("Retention", StringComparison.OrdinalIgnoreCase) || e.Category.Contains("Purge", StringComparison.OrdinalIgnoreCase)),
                "an Error log of the retention service about the failed purge");
            Assert.NotNull(logged);

            using var client = host.NewClient();
            using var response = await client.PostAsync("/api/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // the host serves requests

            Assert.Equal(4, await ErrorLogDb.CountAsync(database.ConnectionString)); // and nothing was half-deleted
        }
        finally
        {
            host.Factory.Dispose();
        }
    }
}
