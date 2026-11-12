using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Billing;

/// <summary><c>Billing:Job:*</c>: the billing job's switch (off in the test hosts, which run it by hand), pace and batch.</summary>
public sealed record BillingJobOptions(bool Enabled, int IntervalSeconds, int BatchSize, int EventMaxAttempts)
{
    public static BillingJobOptions Default { get; } = new(true, 60, 100, 8);
}

/// <summary>Wakes the billing job of this instance, so an event stored by the endpoint is processed at once instead of at the next run.</summary>
public sealed class BillingJobSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// One run of the billing job (docs/BILLING.md, "The billing job"): the stored gateway events first (each claimed with
/// <c>FOR UPDATE SKIP LOCKED</c>, its transaction re-read from the gateway and applied), then every subscription whose <c>NextRunAt</c> passed
/// (each in a scope of its own, through <see cref="BillingEngine.RunAsync"/>, which skips a subscription another instance holds). Safe with
/// several instances; a failure of one company never stops the others.
/// </summary>
public sealed class BillingJobRunner
{
    public static readonly TimeSpan EventLease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan EventRetryBase = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly BillingJobOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingJobRunner> _logger;

    public BillingJobRunner(IServiceScopeFactory scopes, BillingJobOptions options, TimeProvider time, ILogger<BillingJobRunner> logger)
    {
        _scopes = scopes;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await ProcessEventsAsync(cancellationToken);
        await ProcessDueAsync(cancellationToken);
        await HealOrphanSuspensionsAsync(cancellationToken);
    }

    /// <summary>Companies left suspended for billing whose subscription is cancelled: nothing is owed, so they are lifted (docs/BILLING.md).</summary>
    public async Task HealOrphanSuspensionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> orphans;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            orphans = await scope.ServiceProvider.GetRequiredService<IBillingRepository>().ListOrphanBillingSuspensionsAsync(_options.BatchSize, cancellationToken);
        }

        foreach (var tenantId in orphans)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingEngine>().ApplyDunningAsync(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Lifting the orphan billing suspension of tenant {TenantId} failed: {Error}.", tenantId, WebhookErrors.Describe(ex));
            }
        }
    }

    public async Task<int> ProcessEventsAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        while (processed < _options.BatchSize && await ProcessOneEventAsync(cancellationToken))
        {
            processed++;
        }

        return processed;
    }

    public async Task ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> due;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IBillingRepository>().ListDueAsync(Now, _options.BatchSize, cancellationToken);
        }

        foreach (var tenantId in due)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingEngine>().RunAsync(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("The billing run of tenant {TenantId} failed: {Error}.", tenantId, WebhookErrors.Describe(ex));
            }
        }
    }

    private async Task<bool> ProcessOneEventAsync(CancellationToken cancellationToken)
    {
        BillingEvent? claimed;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = Now;
            claimed = (await db.BillingEvents
                .FromSql($"""
                    SELECT * FROM "BillingEvents" WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
                    ORDER BY "NextAttemptAt" LIMIT 1 FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken)).FirstOrDefault();
            if (claimed is null)
            {
                return false;
            }

            claimed.BeginAttempt(now, EventLease);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        BillingEventStatus? outcome = null;
        string? error = null;
        try
        {
            outcome = await ApplyEventAsync(claimed, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex is BillingException billing ? billing.Code : WebhookErrors.Describe(ex);
            _logger.LogWarning("Billing event {EventId} could not be processed: {Error}.", claimed.Id, error);
        }

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.BillingEvents.FirstAsync(e => e.Id == claimed.Id, cancellationToken);
            if (outcome is { } done)
            {
                stored.Complete(done, Now);
            }
            else
            {
                stored.RegisterFailure(error ?? "failed", Now, _options.EventMaxAttempts, EventRetryBase);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task<BillingEventStatus> ApplyEventAsync(BillingEvent stored, CancellationToken cancellationToken)
    {
        if (stored.EventType != "transaction.updated" || stored.TransactionId is not { } transactionId)
        {
            return BillingEventStatus.Ignored;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var gateway = services.GetRequiredService<IPaymentGateway>();
        var billing = services.GetRequiredService<IBillingRepository>();
        var engine = services.GetRequiredService<BillingEngine>();

        // Never the event's amount or status: the transaction as the gateway has it now.
        var transaction = await gateway.GetTransactionAsync(transactionId, cancellationToken)
                          ?? throw new InvalidOperationException("The gateway does not know the transaction of the event.");

        var attempt = await billing.FindAttemptByTransactionAsync(transaction.Id, cancellationToken)
                      ?? (transaction.Reference is { } reference ? await billing.FindAttemptByReferenceAsync(reference, cancellationToken) : null);
        if (attempt is { } known)
        {
            await engine.ApplyAsync(known.TenantId, known.AttemptId, transaction, cancellationToken);
            return BillingEventStatus.Processed;
        }

        // Only the link the gateway reports on the re-read transaction: never the event's word for it.
        if (transaction.PaymentLinkId is { } linkId && await billing.FindChargeByPaymentLinkAsync(linkId, cancellationToken) is { } charge)
        {
            await engine.ApplyAsync(charge.TenantId, null, transaction, cancellationToken, paymentLinkChargeId: charge.ChargeId);
            return BillingEventStatus.Processed;
        }

        if (transaction.Status == PaymentAttemptStatus.Approved)
        {
            // Money arrived for something we cannot match (an old link, another integration of the merchant): a durable item for a person,
            // kept after the event itself is purged.
            _logger.LogError(BillingEngine.PaymentNeedsReview, "Approved transaction {TransactionId} ({AmountInCents} {Currency}) matches no charge.", transaction.Id, transaction.AmountInCents, transaction.Currency);
            await billing.AddReviewItemAsync(
                BillingReviewItem.Flag("unmatched_payment", _time.GetUtcNow().UtcDateTime, transactionId: transaction.Id, amountInCents: transaction.AmountInCents, currency: transaction.Currency),
                cancellationToken);
        }

        return BillingEventStatus.Ignored;
    }

    /// <summary>Deletes processed and ignored events older than the retention (ids only, but nothing to keep them for).</summary>
    public async Task<int> PurgeOldEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = Now - EventRetention;
        return await db.BillingEvents
            .Where(e => (e.Status == BillingEventStatus.Processed || e.Status == BillingEventStatus.Ignored) && e.ReceivedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>The billing job's loop: a run every <c>Billing:Job:IntervalSeconds</c>, or at once when an event arrives; purges old events hourly.</summary>
public sealed class BillingJobService : BackgroundService
{
    private readonly BillingJobRunner _runner;
    private readonly BillingJobSignal _signal;
    private readonly BillingJobOptions _options;
    private readonly ILogger<BillingJobService> _logger;

    public BillingJobService(BillingJobRunner runner, BillingJobSignal signal, BillingJobOptions options, ILogger<BillingJobService> logger)
    {
        _runner = runner;
        _signal = signal;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunOnceAsync(stoppingToken);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(1))
                {
                    await _runner.PurgeOldEventsAsync(stoppingToken);
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError("The billing job run failed: {Error}.", WebhookErrors.Describe(ex));
            }

            await _signal.WaitAsync(TimeSpan.FromSeconds(_options.IntervalSeconds), stoppingToken);
        }
    }
}
