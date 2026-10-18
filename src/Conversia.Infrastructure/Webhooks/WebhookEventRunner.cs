using System.Text.RegularExpressions;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.ErrorLogging;
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
/// <list type="bullet">
/// <item>The claim is <c>SELECT ... FOR UPDATE SKIP LOCKED</c> inside the transaction that also processes the event: a row another
/// worker is working on is skipped, never waited for, and a crash simply releases the lock so the event is claimable again.</item>
/// <item>Processing runs in a savepoint. On failure the work is rolled back to it, the failure is recorded on the event (attempt count,
/// exponential back-off, sanitized error) and that is committed: the event keeps its lock the whole time, so no other worker can
/// slip in between the failure and its bookkeeping. At the attempt limit the event becomes <c>Failed</c> and is also registered in
/// the error registry.</item>
/// <item>Processing of one tenant is serialized with a transaction-level advisory lock, so two workers never race to create the same
/// contact or conversation (the unique indexes remain the backstop). Different tenants run in parallel.</item>
/// <item>The tenant of the event is set on the scope before any tenant-filtered query runs (<see cref="ITenantContext"/>): the worker
/// has no JWT, and a leak across tenants would need that filter to be bypassed.</item>
/// </list>
/// Every call uses a DI scope (and DbContext) of its own.
/// </summary>
public sealed partial class WebhookEventRunner(
    IServiceScopeFactory scopeFactory,
    WebhookProcessingOptions options,
    IErrorRegistry errorRegistry,
    TimeProvider timeProvider,
    ILogger<WebhookEventRunner> logger)
{
    private const string SavepointName = "process_webhook_event";

    // Well below Npgsql's 30 s command timeout, so the wait for the tenant lock fails as a PostgreSQL error first.
    private const string LockTimeoutSql = "SET LOCAL lock_timeout = '20s'";
    private const string LockTimeoutResetSql = "SET LOCAL lock_timeout = 0";

    /// <summary>Returns true when an event was claimed (processed, or its failure recorded), false when none was due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookEventProcessor>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = UtcNow();

        // Across all tenants on purpose: the queue is shared. LIMIT is inside the locking query, so only the claimed row is locked.
        var claimed = await db.WebhookEvents
            .FromSql($"""
                SELECT * FROM "WebhookEvents"
                WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
                ORDER BY "NextAttemptAt", "ReceivedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            return false;
        }

        var webhookEvent = claimed[0];
        tenantContext.SetTenant(webhookEvent.TenantId);
        await AcquireTenantLockAsync(db, webhookEvent.TenantId, cancellationToken);

        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);
        try
        {
            var result = await processor.ProcessAsync(webhookEvent, cancellationToken);
            webhookEvent.MarkProcessed(UtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Webhook event {EventId} processed: {Stored} messages stored, {AlreadyStored} already stored, {Ignored} ignored.",
                webhookEvent.Id, result.MessagesStored, result.MessagesAlreadyStored, result.Ignored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackToSavepointAsync(SavepointName, CancellationToken.None);
            await RecordFailureAsync(db, transaction, webhookEvent.Id, webhookEvent.TenantId, ex);
        }

        return true;
    }

    private async Task RecordFailureAsync(ApplicationDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, Guid eventId, Guid tenantId, Exception exception)
    {
        // Drop whatever the failed attempt tracked, then reload the event: only the failure bookkeeping may be saved.
        db.ChangeTracker.Clear();
        var webhookEvent = await db.WebhookEvents.SingleAsync(e => e.Id == eventId);

        var error = Describe(exception);
        webhookEvent.RegisterFailure(error, UtcNow(), options.MaxAttempts, options.RetryBaseDelay);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        if (webhookEvent.Status == WebhookEventStatus.Failed)
        {
            logger.LogError("Webhook event {EventId} failed for good after {Attempts} attempts: {Error}", eventId, webhookEvent.Attempts, error);
            await errorRegistry.RegisterAsync(new ErrorReport
            {
                OccurredAt = UtcNow(),
                Source = ErrorSource.Api,
                Kind = ErrorKind.Unhandled,
                Path = "webhook-worker",
                TenantId = tenantId,
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                ExceptionMessage = error,
                Context = $"WebhookEvent {eventId} failed after {webhookEvent.Attempts} attempts.",
            });
        }
        else
        {
            logger.LogWarning(
                "Webhook event {EventId} failed (attempt {Attempts} of {MaxAttempts}); next attempt at {NextAttemptAt:O}: {Error}",
                eventId, webhookEvent.Attempts, options.MaxAttempts, webhookEvent.NextAttemptAt, error);
        }
    }

    private static async Task AcquireTenantLockAsync(ApplicationDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        // SET LOCAL takes no parameters; the values are constants. The timeout bounds the wait and is lifted once the lock is held.
        await db.Database.ExecuteSqlRawAsync(LockTimeoutSql, cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId.ToString()}, 0))", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(LockTimeoutResetSql, cancellationToken);
    }

    private DateTime UtcNow() => DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);

    // What is kept on the event and in the error registry: the exception type and its innermost message, sanitized like every stored
    // error (secrets redacted, control characters stripped, cut) and with long digit runs (phone numbers) masked: a database error can quote values.
    private static string Describe(Exception exception)
    {
        var innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        var text = $"{exception.GetType().Name}: {innermost.Message}";
        return ErrorSanitizer.SanitizeText(LongNumber().Replace(text, "[num]"), WebhookEvent.LastErrorMaxLength) ?? string.Empty;
    }

    [GeneratedRegex(@"\+?\d{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();
}
