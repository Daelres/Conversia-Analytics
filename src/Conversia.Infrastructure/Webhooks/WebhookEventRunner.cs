using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Webhooks;

/// <summary>
/// Claims and processes one pending webhook event at a time. Safe to run in several workers and several API instances at once.
/// <list type="number">
/// <item><b>Claim</b> (short transaction): <c>SELECT ... FOR UPDATE SKIP LOCKED</c> takes the next due event; a row another worker is claiming is
/// skipped, never waited for. The attempt is counted and the event is <b>leased</b> (<c>NextAttemptAt</c> moves to now plus the lease) in
/// that same committed step, before any work. Nobody else can claim it meanwhile; if the process dies, the event becomes claimable again with
/// the attempt already counted, so a payload that kills the worker ends up Failed instead of looping forever. An event that has used its
/// attempts without ever finishing is marked Failed on the claim.</item>
/// <item><b>Process</b> (second transaction, bounded by <c>statement_timeout</c>, <c>idle_in_transaction_session_timeout</c> and a total
/// budget): the tenant is set, a per-tenant advisory lock is <i>tried</i> (never waited for beyond a short spin): if the tenant is busy the
/// event is deferred a few seconds <b>without consuming the attempt</b> and the worker moves on to other tenants. The delivery is processed
/// and the event marked Processed in one commit; any exception rolls the whole transaction back.</item>
/// <item><b>Failure bookkeeping</b> (fresh transaction on a fresh context, no tenant context needed): whatever went wrong, anywhere after the
/// claim, records the sanitized error and the exponential back-off, or Failed at the attempt limit (also registered in the error registry).
/// It must be able to succeed when the processing transaction is dead, and it never throws out of the worker.</item>
/// </list>
/// Processing of one tenant is serialized by the advisory lock so two workers never race to create the same contact or conversation (the
/// unique indexes remain the backstop); different tenants run in parallel. Every call uses DI scopes (and DbContexts) of its own.
/// </summary>
public sealed class WebhookEventRunner(
    IServiceScopeFactory scopeFactory,
    WebhookProcessingOptions options,
    IErrorRegistry errorRegistry,
    TimeProvider timeProvider,
    ILogger<WebhookEventRunner> logger)
{
    private const int TenantLockPollMilliseconds = 25;

    /// <summary>Returns true when an event was claimed (processed, deferred, or its failure recorded), false when none was due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimAsync(cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        if (claimed.AlreadyHandled)
        {
            return true;
        }

        try
        {
            await ProcessClaimedAsync(claimed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping. The lease expires and the event is claimed again, with this attempt counted.
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(claimed.EventId, claimed.TenantId, ex);
        }

        return true;
    }

    private sealed record Claimed(Guid EventId, Guid TenantId, bool AlreadyHandled = false);

    private async Task<Claimed?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SetTimeoutsAsync(db, cancellationToken);

        var now = UtcNow();

        // Across all tenants on purpose: the queue is shared. LIMIT is inside the locking query, so only the claimed row is locked.
        // Only the events of companies that are Active: those of a pending or suspended one stay pending, untouched, and are picked up
        // again when the company is activated (the EXISTS is not in the FROM, so it locks nothing of Tenants).
        var active = nameof(TenantStatus.Active);
        var claimed = await db.WebhookEvents
            .FromSql($"""
                SELECT * FROM "WebhookEvents"
                WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
                  AND EXISTS (SELECT 1 FROM "Tenants" WHERE "Tenants"."Id" = "WebhookEvents"."TenantId" AND "Tenants"."Status" = {active})
                ORDER BY "NextAttemptAt", "ReceivedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            return null;
        }

        var webhookEvent = claimed[0];
        if (webhookEvent.Attempts >= options.MaxAttempts)
        {
            // Its attempts were all started and none finished: the worker died or timed out each time. Stop feeding it to workers.
            webhookEvent.Fail("The event used all its processing attempts without finishing (the worker may have crashed or timed out while processing it).");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ReportFailedAsync(webhookEvent.Id, webhookEvent.TenantId, webhookEvent.Attempts, "WorkerLostEvent", webhookEvent.LastError!);
            return new Claimed(webhookEvent.Id, webhookEvent.TenantId, AlreadyHandled: true);
        }

        webhookEvent.BeginAttempt(now, options.Lease);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Claimed(webhookEvent.Id, webhookEvent.TenantId);
    }

    private async Task ProcessClaimedAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        // The whole processing, lock wait included, fits well inside the lease.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(claimed.TenantId);
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookEventProcessor>();

        await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
        await SetTimeoutsAsync(db, budget.Token);

        if (!await TryLockTenantAsync(db, claimed.TenantId, budget.Token))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DeferAsync(claimed.EventId);
            return;
        }

        var webhookEvent = await db.WebhookEvents.SingleAsync(e => e.Id == claimed.EventId, budget.Token);
        if (webhookEvent.Status != WebhookEventStatus.Pending)
        {
            return; // finished meanwhile by another worker whose lease on it had expired
        }

        WebhookProcessingResult result;
        try
        {
            result = await processor.ProcessAsync(webhookEvent, budget.Token);
        }
        catch (WebhookPermanentFailureException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await FailPermanentlyAsync(claimed.EventId, claimed.TenantId, ex);
            return;
        }

        webhookEvent.RecordParseCounts(result.Skipped, result.Statuses, result.MessagesStored, result.MessagesAlreadyStored);
        webhookEvent.MarkProcessed(UtcNow());
        await db.SaveChangesAsync(budget.Token);
        await transaction.CommitAsync(budget.Token);

        logger.LogInformation(
            "Webhook event {EventId} processed: {Stored} messages stored, {AlreadyStored} already stored, {Ignored} ignored.",
            webhookEvent.Id, result.MessagesStored, result.MessagesAlreadyStored, result.Ignored);
        if (result.Skipped > 0)
        {
            // No content here, on purpose: the skipped entries hold phone numbers or ids of customers.
            logger.LogWarning(
                "Webhook event {EventId} of tenant {TenantId} had {Skipped} messages that could not be stored (no usable phone number, id or timestamp, a group, or another number).",
                webhookEvent.Id, webhookEvent.TenantId, result.Skipped);
        }
    }

    // The tenant advisory lock is tried, never waited for in the database: a short spin, then the caller defers the event so that the
    // worker goes on with other tenants instead of sitting on this one.
    //
    // It is also why two ingestion workers cannot deadlock on the conversation rows they lock afterwards (FindOpenAsync, FOR UPDATE): they are
    // taken only while holding this lock, so two transactions of one tenant never run together, and conversations never cross tenants. The jobs
    // that do touch conversation rows meanwhile (closing, classification) use SKIP LOCKED and never wait. See docs/WEBHOOKS.md, "Lock order".
    private async Task<bool> TryLockTenantAsync(ApplicationDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var key = tenantId.ToString();
        var deadline = timeProvider.GetTimestamp();
        while (true)
        {
            var acquired = await db.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (acquired)
            {
                return true;
            }

            if (timeProvider.GetElapsedTime(deadline) >= options.TenantLockWait)
            {
                return false;
            }

            await Task.Delay(TenantLockPollMilliseconds, cancellationToken);
        }
    }

    private async Task SetTimeoutsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // set_config(..., is_local = true) is SET LOCAL with a parameter: the settings end with the transaction.
        var value = $"{options.ProcessingTimeoutSeconds}s";
        await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {value}, true)", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('idle_in_transaction_session_timeout', {value}, true)", cancellationToken);
    }

    // ---- Bookkeeping: fresh scope, fresh transaction, no tenant context (the event is addressed by id). Never throws. ----

    private async Task DeferAsync(Guid eventId)
    {
        // 50% to 150% of the base delay, so deferred events of one busy tenant do not all come back at the same instant.
        var delay = TimeSpan.FromSeconds(options.TenantBusyDelaySeconds * (0.5 + Random.Shared.NextDouble()));
        await BookkeepAsync(eventId, "defer", (webhookEvent, now) => webhookEvent.Defer(now, delay));
        logger.LogInformation("Webhook event {EventId} deferred {Delay:0.#} s: its tenant is being processed by another worker.", eventId, delay.TotalSeconds);
    }

    private async Task FailPermanentlyAsync(Guid eventId, Guid tenantId, Exception exception)
    {
        var error = WebhookErrors.Describe(exception);
        var attempts = 0;
        await BookkeepAsync(eventId, "fail", (webhookEvent, _) =>
        {
            webhookEvent.Fail(error);
            attempts = webhookEvent.Attempts;
        });
        await ReportFailedAsync(eventId, tenantId, attempts, exception.GetType().FullName ?? exception.GetType().Name, error);
    }

    private async Task RecordFailureAsync(Guid eventId, Guid tenantId, Exception exception)
    {
        var error = WebhookErrors.Describe(exception);
        var failedForGood = false;
        var attempts = 0;
        var next = default(DateTime);

        var recorded = await BookkeepAsync(eventId, "record the failure of", (webhookEvent, now) =>
        {
            webhookEvent.RegisterFailure(error, now, options.MaxAttempts, options.RetryBaseDelay);
            failedForGood = webhookEvent.Status == WebhookEventStatus.Failed;
            attempts = webhookEvent.Attempts;
            next = webhookEvent.NextAttemptAt;
        });

        if (!recorded)
        {
            return; // the lease will bring the event back; the attempt it used stays counted
        }

        if (failedForGood)
        {
            await ReportFailedAsync(eventId, tenantId, attempts, exception.GetType().FullName ?? exception.GetType().Name, error);
        }
        else
        {
            logger.LogWarning(
                "Webhook event {EventId} failed (attempt {Attempts} of {MaxAttempts}); next attempt at {NextAttemptAt:O}: {Error}",
                eventId, attempts, options.MaxAttempts, next, error);
        }
    }

    private async Task ReportFailedAsync(Guid eventId, Guid tenantId, int attempts, string exceptionType, string error)
    {
        logger.LogError("Webhook event {EventId} failed for good after {Attempts} attempts: {Error}", eventId, attempts, error);
        await errorRegistry.RegisterAsync(new ErrorReport
        {
            OccurredAt = UtcNow(),
            Source = ErrorSource.Api,
            Kind = ErrorKind.Unhandled,
            Path = "webhook-worker",
            TenantId = tenantId,
            ExceptionType = exceptionType,
            ExceptionMessage = error,
            Context = $"WebhookEvent {eventId} failed after {attempts} attempts.",
        });
    }

    private async Task<bool> BookkeepAsync(Guid eventId, string what, Action<WebhookEvent, DateTime> change)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await SetTimeoutsAsync(db, CancellationToken.None);

            var webhookEvent = await db.WebhookEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == eventId, CancellationToken.None);
            change(webhookEvent, UtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("Could not {What} webhook event {EventId}; its lease will bring it back: {Error}", what, eventId, WebhookErrors.Describe(ex));
            return false;
        }
    }

    private DateTime UtcNow() => DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);
}
