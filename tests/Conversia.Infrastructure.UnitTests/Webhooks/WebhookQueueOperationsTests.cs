using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Webhooks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;

namespace Conversia.Infrastructure.UnitTests.Webhooks;

public class WebhookQueueOperationsTests
{
    private static readonly WebhookHealthOptions Options = new(MaxPendingAgeMinutes: 10, MaxFailedPerHour: 5);

    private static Mock<IWebhookQueueMonitor> MonitorReturning(WebhookQueueSnapshot snapshot)
    {
        var monitor = new Mock<IWebhookQueueMonitor>();
        monitor.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        return monitor;
    }

    private static Task<HealthCheckResult> CheckAsync(IWebhookQueueMonitor monitor)
        => new WebhookQueueHealthCheck(monitor, Options, Mock.Of<ILogger<WebhookQueueHealthCheck>>()).CheckHealthAsync(new HealthCheckContext());

    // ---- Health ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, null, 0, HealthStatus.Healthy)]
    [InlineData(500, 9 * 60, 5, HealthStatus.Healthy)] // a big backlog that is moving, and a failure count at the threshold
    [InlineData(1, 10 * 60 + 1, 0, HealthStatus.Degraded)] // the oldest event has waited more than 10 minutes
    [InlineData(1, 10 * 60, 0, HealthStatus.Healthy)]
    [InlineData(0, null, 6, HealthStatus.Degraded)] // more than 5 failures in the last hour
    [InlineData(3, 3600, 100, HealthStatus.Degraded)]
    public async Task TheQueueIsDegradedWhenTheOldestEventIsTooOldOrTooManyFailed(int pending, int? oldestSeconds, int failed, HealthStatus expected)
    {
        var monitor = MonitorReturning(new WebhookQueueSnapshot(pending, oldestSeconds is null ? null : TimeSpan.FromSeconds(oldestSeconds.Value), failed));

        Assert.Equal(expected, (await CheckAsync(monitor.Object)).Status);
    }

    [Fact]
    public async Task AQueueThatCannotBeRead_IsUnhealthy_AndTheDescriptionLeaksNothing()
    {
        var monitor = new Mock<IWebhookQueueMonitor>();
        monitor.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("connection to 573005550101 refused"));

        var result = await CheckAsync(monitor.Object);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("573005550101", result.Description);
        Assert.Null(result.Exception);
    }

    // ---- Backlog log: at most once per minute -----------------------------------------------------------------

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)by.TotalMilliseconds;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task TheBacklogIsLoggedAtInformationAtMostOncePerMinute()
    {
        var time = new ManualTime();
        var logger = new RecordingLogger<WebhookQueueBacklogReporter>();
        var monitor = MonitorReturning(new WebhookQueueSnapshot(7, TimeSpan.FromSeconds(42), 1));
        var reporter = new WebhookQueueBacklogReporter(monitor.Object, time, logger);

        await reporter.ReportIfDueAsync();
        await reporter.ReportIfDueAsync();
        time.Advance(TimeSpan.FromSeconds(59));
        await reporter.ReportIfDueAsync();
        Assert.Single(logger.Entries);

        time.Advance(TimeSpan.FromSeconds(2));
        await reporter.ReportIfDueAsync();

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Contains("7 pending", logger.Entries[0].Message);
        monitor.Verify(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AFailingBacklogRead_IsLoggedMaskedAndNotRetriedBeforeTheMinuteIsUp()
    {
        var time = new ManualTime();
        var logger = new RecordingLogger<WebhookQueueBacklogReporter>();
        var monitor = new Mock<IWebhookQueueMonitor>();
        monitor.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("failed for 573005550101"));
        var reporter = new WebhookQueueBacklogReporter(monitor.Object, time, logger);

        await reporter.ReportIfDueAsync();
        await reporter.ReportIfDueAsync();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain("573005550101", entry.Message);
    }
}
