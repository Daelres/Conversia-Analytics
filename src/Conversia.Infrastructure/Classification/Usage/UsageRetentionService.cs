using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Classification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification.Usage;

/// <summary>
/// Keeps the usage tables small: the AI distinct conversation sets older than the previous month and the send counters older than 90 days are
/// deleted, once at startup and then every 6 hours. The AI month rows (counts, tokens, USD) are kept. A failure is logged and retried at the
/// next run.
/// </summary>
public sealed class UsageRetentionService(
    AiUsageMeter meter, MessageSendOutcomeRecorder outcomes, TimeProvider timeProvider, ILogger<UsageRetentionService> logger, LlmAnalysisRunner? batches = null)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    internal async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sets = await meter.PurgeConversationSetsAsync(cancellationToken);
            var counters = await outcomes.PurgeAsync(cancellationToken);
            // The resolved Message Batches (Classification:Llm:Batch:RetentionDays): they only steered the collection.
            var resolved = batches is null ? 0 : await batches.PurgeResolvedBatchesAsync(cancellationToken);
            if (sets + counters + resolved > 0)
            {
                logger.LogInformation(
                    "Usage retention deleted {Sets} AI conversation-set rows, {Counters} send counters and {Batches} resolved batches.", sets, counters, resolved);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("The usage retention purge failed; it will be tried again at the next run: {Error}", WebhookErrors.Describe(ex));
        }
    }
}
