using Conversia.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.UnitTests.Messaging;

/// <summary>
/// The per-company send limits in memory: the administrator's change on this instance applies at once, the periodic reload brings the
/// others' changes, and a reload that read the database before a change was committed never overwrites it.
/// </summary>
public class TenantSendRateLimitsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed class NoScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database unreachable");
    }

    private static TenantSendRateLimits New() => new(new NoScopes());

    private static Task<IReadOnlyList<(Guid TenantId, int Limit)>> Rows(params (Guid, int)[] rows) => Task.FromResult<IReadOnlyList<(Guid, int)>>(rows);

    [Fact]
    public async Task AReload_ReplacesTheLimits_WithWhatTheDatabaseHolds()
    {
        var limits = New();
        limits.Update(Tenant, 40);
        var other = Guid.NewGuid();

        await limits.ReloadAsync(_ => Rows((other, 7)));

        Assert.Equal((null, (int?)7), (limits.OverrideFor(Tenant), limits.OverrideFor(other)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public async Task AReloadThatReadBeforeAChange_NeverOverwritesIt(int? changedTo)
    {
        var limits = New();
        var read = new TaskCompletionSource<IReadOnlyList<(Guid, int)>>();
        var reload = limits.ReloadAsync(_ => read.Task);

        limits.Update(Tenant, changedTo); // committed and applied while the reload's read was in flight
        read.SetResult([(Tenant, 99)]);   // the read saw the old value
        await reload;

        Assert.Equal(changedTo, limits.OverrideFor(Tenant));
    }

    [Fact]
    public async Task OnceAReloadStartedAfterTheChange_TheDatabaseIsTheTruthAgain()
    {
        var limits = New();
        limits.Update(Tenant, 5);

        await limits.ReloadAsync(_ => Rows((Tenant, 99))); // another instance changed it since

        Assert.Equal(99, limits.OverrideFor(Tenant));
    }

    [Fact]
    public async Task AFailedReload_KeepsTheValuesItHad()
    {
        var limits = New();
        limits.Update(Tenant, 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => limits.ReloadAsync());

        Assert.Equal(5, limits.OverrideFor(Tenant));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task TheRefresher_LogsAFailedReload_AndKeepsRunning()
    {
        var limits = New();
        limits.Update(Tenant, 5);
        var logger = new ListLogger<TenantSendRateLimitRefresher>();
        using var refresher = new TenantSendRateLimitRefresher(limits, TimeProvider.System, logger);

        await refresher.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && logger.Entries.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        await refresher.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("could not be reloaded", StringComparison.Ordinal));
        Assert.Equal(5, limits.OverrideFor(Tenant));
    }
}
