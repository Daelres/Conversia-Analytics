using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The background paths of the usage tables, called directly (the shared hosts keep them switched off): the retention deletes exactly what it
/// should, a failure is logged and the next run tries again, and the send counter never disturbs a send.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class UsageRetentionTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UsageRetentionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private IServiceScopeFactory Scopes => _factory.Services.GetRequiredService<IServiceScopeFactory>();

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public int Attempts;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("database unreachable");
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task TheRetention_DeletesOnlyOldSetsAndOldCounters()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "retention");
        var now = DateTime.UtcNow;
        var settings = AiQuotaSettings.Default;
        var meterAt = (DateTime at) => new AiUsageMeter(Scopes, settings, new FixedTimeProvider(at), NullLogger<AiUsageMeter>.Instance);
        var recorderAt = (DateTime at) => new MessageSendOutcomeRecorder(Scopes, settings, new FixedTimeProvider(at), NullLogger<MessageSendOutcomeRecorder>.Instance);
        foreach (var back in new[] { 2, 1, 0 })
        {
            await meterAt(now.AddMonths(-back)).RecordSuccessAsync(new AiUsageRecord(tenant.TenantId, Guid.NewGuid(), false, new LlmUsage(1, 1, 0, 0), 0m));
        }

        await recorderAt(now.AddDays(-(MessageSendOutcomeRecorder.RetentionDays + 1))).RecordAsync(tenant.TenantId, false, "sent");
        await recorderAt(now.AddDays(-(MessageSendOutcomeRecorder.RetentionDays - 1))).RecordAsync(tenant.TenantId, false, "sent");

        var clock = new FixedTimeProvider(now);
        await new UsageRetentionService(meterAt(now), recorderAt(now), clock, NullLogger<UsageRetentionService>.Instance).PurgeOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var month = settings.MonthOf(now);
        Assert.Equal([month.AddMonths(-1), month],
            await db.AiUsageConversations.IgnoreQueryFilters().Where(c => c.TenantId == tenant.TenantId).Select(c => c.Month).OrderBy(m => m).ToListAsync());
        Assert.Equal(3, await db.AiUsageMonths.IgnoreQueryFilters().CountAsync(u => u.TenantId == tenant.TenantId));
        Assert.Equal([settings.DayOf(now).AddDays(-(MessageSendOutcomeRecorder.RetentionDays - 1))],
            await db.MessageSendOutcomes.IgnoreQueryFilters().Where(o => o.TenantId == tenant.TenantId).Select(o => o.Day).ToListAsync());
    }

    [Fact]
    public async Task AFailedRetention_IsLogged_AndTheNextRunTriesAgain()
    {
        var broken = new BrokenScopes();
        var logger = new ListLogger<UsageRetentionService>();
        var service = new UsageRetentionService(
            new AiUsageMeter(broken, AiQuotaSettings.Default, TimeProvider.System, NullLogger<AiUsageMeter>.Instance),
            new MessageSendOutcomeRecorder(broken, AiQuotaSettings.Default, TimeProvider.System, NullLogger<MessageSendOutcomeRecorder>.Instance),
            TimeProvider.System, logger);

        await service.PurgeOnceAsync(CancellationToken.None);
        await service.PurgeOnceAsync(CancellationToken.None);

        Assert.Equal(2, broken.Attempts);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Error));
    }

    [Fact]
    public async Task TheSendCounter_SwallowsItsOwnFailure()
    {
        var logger = new ListLogger<MessageSendOutcomeRecorder>();
        var recorder = new MessageSendOutcomeRecorder(new BrokenScopes(), AiQuotaSettings.Default, TimeProvider.System, logger);

        await recorder.RecordAsync(Guid.NewGuid(), false, "sent");

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
