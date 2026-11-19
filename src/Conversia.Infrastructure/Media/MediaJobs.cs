using Conversia.Application.Media;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Media;

/// <summary>
/// Downloads received media, one message per call, in three steps like the webhook worker (docs/MESSAGING.md, "Media"):
/// <list type="number">
/// <item><b>Claim</b> (short transaction): the oldest due pending media of an Active company, <c>FOR UPDATE SKIP LOCKED</c>. Too old for the
/// provider (<see cref="MediaOptions.DownloadWindowHours"/>) is <c>Expired</c>; out of attempts is <c>Failed</c>; otherwise the attempt is
/// counted and the row leased (<see cref="MediaOptions.LeaseMinutes"/>), so a crash only delays it.</item>
/// <item><b>Download and check</b>, with no transaction open: the channel's key (or the internal sandbox client), the limit of the kind, the
/// content against the allow-list, then the copy into the store.</item>
/// <item><b>Record</b> (short transaction): the row is taken again and the outcome applied only if it is still the claimed attempt; otherwise (the
/// message was deleted meanwhile, by a suppression for example) the stored copy is deleted again.</item>
/// </list>
/// </summary>
public sealed class MediaDownloadRunner(
    IServiceScopeFactory scopes, IMediaStore store, IWhatsAppMediaClientResolver clients, MediaOptions options, TimeProvider time, ILogger<MediaDownloadRunner> logger)
{
    private sealed record Claim(Guid Id, Guid TenantId, Guid ChannelId, bool IsSandbox, string MediaId, string Type, string? DeclaredMime, int Attempt);

    /// <summary>Processes one due media; false when none is due.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var claim = await ClaimAsync(cancellationToken);
        if (claim is null)
        {
            return false;
        }

        if (claim.Value.Work is not { } work)
        {
            return true; // settled during the claim (expired, out of attempts)
        }

        var outcome = await FetchAsync(work, cancellationToken);
        await RecordAsync(work, outcome, cancellationToken);
        return true;
    }

    private async Task<(Claim? Work, bool)?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT m."Id" AS "Value" FROM "Messages" m
            JOIN "Tenants" t ON t."Id" = m."TenantId"
            WHERE m."MediaStatus" = 'Pending' AND t."Status" = 'Active'
              -- due, or never tried yet (a new message is due at once, even when its time is a second ahead of ours; a released attempt is not)
              AND (m."MediaNextAttemptAt" <= {now} OR (m."MediaAttempts" = 0 AND m."MediaNextAttemptAt" = m."SentAt"))
            ORDER BY m."MediaNextAttemptAt"
            LIMIT 1
            FOR UPDATE OF m SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return null;
        }

        var message = await db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == ids[0], cancellationToken);
        Claim? work = null;
        if (now - message.CreatedAt > TimeSpan.FromHours(options.DownloadWindowHours) || message.MediaId is null)
        {
            message.MarkMediaUnavailable(MediaStatus.Expired);
        }
        else if (message.MediaAttempts >= options.MaxDownloadAttempts)
        {
            message.MarkMediaUnavailable(MediaStatus.Failed);
        }
        else
        {
            message.ScheduleMediaRetry(now.AddMinutes(options.LeaseMinutes));
            var isSandbox = await db.Conversations.IgnoreQueryFilters().Where(c => c.Id == message.ConversationId).Select(c => c.IsSandbox).SingleAsync(cancellationToken);
            work = new Claim(message.Id, message.TenantId, message.ChannelId, isSandbox, message.MediaId, message.Type, message.MediaMimeType, message.MediaAttempts);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (work, true);
    }

    private abstract record Outcome;

    private sealed record StoredOutcome(StoredMediaObject Stored) : Outcome;

    private sealed record FinalOutcome(MediaStatus Status, string Reason) : Outcome;

    /// <param name="OursNotTheProviders">The failure was Conversia's (the store): the attempt is given back (see <see cref="Message.ReleaseMediaAttempt"/>).</param>
    private sealed record RetryOutcome(string Reason, bool OursNotTheProviders = false) : Outcome;

    private async Task<Outcome> FetchAsync(Claim work, CancellationToken cancellationToken)
    {
        if (MediaPolicy.KindOf(work.Type) is not { } kind)
        {
            return new FinalOutcome(MediaStatus.Failed, "not_media");
        }

        WhatsAppChannel? channel;
        await using (var scope = scopes.CreateAsyncScope())
        {
            channel = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().WhatsAppChannels.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == work.ChannelId, cancellationToken);
        }

        if (channel is null)
        {
            return new FinalOutcome(MediaStatus.Failed, "channel_gone");
        }

        await using var result = await clients.Resolve(channel, work.IsSandbox).DownloadAsync(channel, work.MediaId, MediaPolicy.MaxBytes(kind), work.DeclaredMime, cancellationToken);
        switch (result.Status)
        {
            case MediaDownloadStatus.Downloaded when result.Content is { CanSeek: true, Length: > 0 } content:
                if (content.Length > MediaPolicy.MaxBytes(kind))
                {
                    return new FinalOutcome(MediaStatus.TooLarge, "too_large");
                }

                var inspection = MediaPolicy.Inspect(kind, result.DeclaredMimeType ?? work.DeclaredMime, MediaPolicy.ReadHead(content));
                if (!inspection.Allowed)
                {
                    return new FinalOutcome(MediaStatus.Blocked, inspection.Reason ?? "blocked");
                }

                if (MediaPolicy.IsOfficeDocument(inspection.ContentType) && MediaPolicy.ContainsMacros(content))
                {
                    return new FinalOutcome(MediaStatus.Blocked, "macros");
                }

                try
                {
                    return new StoredOutcome(await store.PutAsync(work.TenantId, content, inspection.ContentType!, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The store is down, not the provider: the file is still there for days. Tried again later without using up an attempt.
                    logger.LogWarning("The media store could not keep the media of message {MessageId} of tenant {TenantId} ({ErrorType}).", work.Id, work.TenantId, ex.GetType().Name);
                    return new RetryOutcome("store_unavailable", OursNotTheProviders: true);
                }
            case MediaDownloadStatus.Downloaded:
                return new FinalOutcome(MediaStatus.Failed, "empty");
            case MediaDownloadStatus.TooLarge:
                return new FinalOutcome(MediaStatus.TooLarge, "too_large");
            case MediaDownloadStatus.NotFound:
                return new FinalOutcome(MediaStatus.Expired, "not_found");
            case MediaDownloadStatus.Rejected:
                return new FinalOutcome(MediaStatus.Failed, "rejected");
            default:
                return new RetryOutcome(result.Status.ToString());
        }
    }

    // A copy stored for an outcome that could not be recorded must not stay behind: deleted on any failure, whatever the caller's token.
    private async Task RecordAsync(Claim work, Outcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await RecordOnceAsync(work, outcome, cancellationToken);
        }
        catch (Exception ex) when (outcome is StoredOutcome stored)
        {
            try
            {
                await store.DeleteAsync(work.TenantId, stored.Stored.ObjectKey, CancellationToken.None);
            }
            catch (Exception deleteFailure)
            {
                // The orphan sweep removes it later (MediaOrphanSweeper).
                logger.LogWarning("A stored media copy of tenant {TenantId} could not be deleted after its record failed ({ErrorType}).", work.TenantId, deleteFailure.GetType().Name);
            }

            logger.LogWarning("The media of message {MessageId} could not be recorded ({ErrorType}); its copy was removed.", work.Id, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>Test hook: runs inside the record transaction before its commit (to simulate a crash between the store and the record).</summary>
    internal Func<Task>? BeforeRecordCommit { get; set; }

    private async Task RecordOnceAsync(Claim work, Outcome outcome, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Messages
            .FromSql($"""SELECT * FROM "Messages" WHERE "Id" = {work.Id} FOR UPDATE""")
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var message = rows.SingleOrDefault();
        if (message is null || message.MediaStatus != MediaStatus.Pending || message.MediaAttempts != work.Attempt)
        {
            await transaction.CommitAsync(cancellationToken);
            if (outcome is StoredOutcome orphan)
            {
                // The message went away (or another run took over) while the file was fetched: its copy must not stay.
                await store.DeleteAsync(work.TenantId, orphan.Stored.ObjectKey, CancellationToken.None);
            }

            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        switch (outcome)
        {
            case StoredOutcome stored:
                message.MarkMediaStored(stored.Stored, now);
                break;
            case FinalOutcome final:
                message.MarkMediaUnavailable(final.Status);
                logger.LogInformation("The media of message {MessageId} of tenant {TenantId} was not stored: {Status} ({Reason}).", work.Id, work.TenantId, final.Status, final.Reason);
                break;
            case RetryOutcome { OursNotTheProviders: true } ours:
                message.ReleaseMediaAttempt(now + TimeSpan.FromSeconds(options.RetryBaseSeconds * 5));
                logger.LogWarning("The media of message {MessageId} of tenant {TenantId} waits for the store ({Reason}).", work.Id, work.TenantId, ours.Reason);
                break;
            case RetryOutcome retry:
                if (message.MediaAttempts >= options.MaxDownloadAttempts)
                {
                    message.MarkMediaUnavailable(MediaStatus.Failed);
                }
                else
                {
                    message.PostponeMedia(now + options.RetryDelay(message.MediaAttempts));
                }

                logger.LogWarning("The media download of message {MessageId} of tenant {TenantId} failed ({Reason}); attempt {Attempt} of {Max}.",
                    work.Id, work.TenantId, retry.Reason, message.MediaAttempts, options.MaxDownloadAttempts);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (BeforeRecordCommit is { } hook)
        {
            await hook();
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// The retention of stored media (docs/MESSAGING.md, "Media retention"): copies older than the company's retention (its own, else
/// <see cref="MediaOptions.RetentionDays"/>) are marked <c>Purged</c> and queued in <c>MediaObjectDeletions</c> in one statement; then the queue
/// (which the contact suppression and the sandbox clean-up also fill) is drained against the store. Both steps are idempotent.
/// </summary>
public sealed class MediaRetentionRunner(IServiceScopeFactory scopes, IMediaStore store, MediaOptions options, TimeProvider time, ILogger<MediaRetentionRunner> logger)
{
    /// <summary>One purge batch and one drain pass; returns how many objects were deleted from the store.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await PurgeAsync(cancellationToken);
        return await DrainAsync(null, cancellationToken);
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var purged = await db.Database.ExecuteSqlAsync($"""
            WITH due AS (
                SELECT m."Id", m."TenantId", m."MediaObjectKey" FROM "Messages" m
                JOIN "Tenants" t ON t."Id" = m."TenantId"
                WHERE m."MediaStatus" = 'Stored'
                  AND m."MediaStoredAt" < {now} - make_interval(days => COALESCE(t."MediaRetentionDays", {options.RetentionDays}))
                LIMIT {options.RetentionBatchSize}
                FOR UPDATE OF m SKIP LOCKED
            ), purged AS (
                UPDATE "Messages" m SET "MediaStatus" = 'Purged', "MediaObjectKey" = NULL, "UpdatedAt" = {now}
                FROM due WHERE m."Id" = due."Id"
                RETURNING due."TenantId", due."MediaObjectKey"
            )
            INSERT INTO "MediaObjectDeletions" ("Id", "TenantId", "ObjectKey", "CreatedAt")
            SELECT gen_random_uuid(), "TenantId", "MediaObjectKey", {now} FROM purged WHERE "MediaObjectKey" IS NOT NULL
            """, cancellationToken);
        if (purged > 0)
        {
            logger.LogInformation("The media retention purged {Count} stored copies.", purged);
        }
    }

    /// <summary>The most batches one drain takes (bounded, so one run never holds the job for long; the rest goes at the next cycle).</summary>
    public const int MaxDrainBatches = 50;

    /// <summary>
    /// Deletes the queued objects (of one company, or of all) from the store and their queue rows, batch after batch until nothing due is left
    /// (at most <see cref="MaxDrainBatches"/>). A key the store refuses is postponed with a growing wait and never blocks the others.
    /// </summary>
    public async Task<int> DrainAsync(Guid? tenantId, CancellationToken cancellationToken = default)
    {
        if (options.Store == MediaStoreKind.None)
        {
            return 0; // no store to delete from: the queue is kept until one is configured (docs/MESSAGING.md)
        }

        var deleted = 0;
        for (var batchNumber = 0; batchNumber < MaxDrainBatches; batchNumber++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = time.GetUtcNow().UtcDateTime;
            var query = db.MediaObjectDeletions.IgnoreQueryFilters().Where(d => d.NextAttemptAt == null || d.NextAttemptAt <= now);
            var batch = await (tenantId is { } id ? query.Where(d => d.TenantId == id) : query)
                .OrderBy(d => d.CreatedAt).Take(options.RetentionBatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var item in batch)
            {
                try
                {
                    await store.DeleteAsync(item.TenantId, item.ObjectKey, cancellationToken);
                    db.MediaObjectDeletions.Remove(item);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Left in the queue with a growing wait: the next runs try again. Ids and the type only.
                    item.Postpone(now);
                    logger.LogWarning("A stored media object of tenant {TenantId} could not be deleted yet ({ErrorType}); attempt {Attempt}.", item.TenantId, ex.GetType().Name, item.Attempts);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return deleted;
    }
}

/// <summary>
/// Deletes objects of the store that nothing references (docs/MESSAGING.md, "Media retention and deletion"): a process that died between
/// storing a copy and recording it, or a record that failed and could not delete it. Only objects older than a grace period are touched, so a
/// copy whose record is in flight is never taken; a key that a message or the deletion queue names is kept.
/// </summary>
public sealed class MediaOrphanSweeper(IServiceScopeFactory scopes, IMediaStore store, MediaOptions options, ILogger<MediaOrphanSweeper> logger)
{
    /// <summary>Sweeps every company; returns how many objects were deleted.</summary>
    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        if (options.Store == MediaStoreKind.None)
        {
            return 0;
        }

        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(cancellationToken);
        }

        var deleted = 0;
        foreach (var tenantId in tenants)
        {
            var candidates = new List<string>();
            await foreach (var item in store.ListAsync(tenantId, cancellationToken))
            {
                if (item.LastModified < olderThan)
                {
                    candidates.Add(item.ObjectKey);
                }
            }

            for (var start = 0; start < candidates.Count; start += 500)
            {
                var slice = candidates.Skip(start).Take(500).ToList();
                HashSet<string> referenced;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    referenced = (await db.Messages.IgnoreQueryFilters().AsNoTracking()
                            .Where(m => m.TenantId == tenantId && m.MediaObjectKey != null && slice.Contains(m.MediaObjectKey))
                            .Select(m => m.MediaObjectKey!).ToListAsync(cancellationToken))
                        .Concat(await db.MediaObjectDeletions.IgnoreQueryFilters().AsNoTracking()
                            .Where(d => d.TenantId == tenantId && slice.Contains(d.ObjectKey)).Select(d => d.ObjectKey).ToListAsync(cancellationToken))
                        .ToHashSet(StringComparer.Ordinal);
                }

                foreach (var key in slice.Where(k => !referenced.Contains(k)))
                {
                    try
                    {
                        await store.DeleteAsync(tenantId, key, cancellationToken);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning("An orphan media object of tenant {TenantId} could not be deleted ({ErrorType}).", tenantId, ex.GetType().Name);
                    }
                }
            }
        }

        if (deleted > 0)
        {
            logger.LogWarning("The orphan sweep deleted {Count} stored media objects that nothing referenced.", deleted);
        }

        return deleted;
    }
}

/// <summary>
/// Runs, every <see cref="MediaOptions.DownloadIntervalSeconds"/>: the download job until nothing is due, the drain of the deletion queue
/// (always: suppressions and sandbox clean-ups fill it whatever the retention setting), the retention every
/// <see cref="MediaOptions.RetentionIntervalMinutes"/> and the orphan sweep every <see cref="MediaOptions.OrphanSweepIntervalHours"/>.
/// </summary>
public sealed class MediaBackgroundService(
    MediaDownloadRunner downloads, MediaRetentionRunner retention, MediaOptions options, bool downloadEnabled, bool retentionEnabled, ILogger<MediaBackgroundService> logger,
    MediaOrphanSweeper? sweeper = null, TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _nextRetention = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    /// <summary>One cycle of every job (public for the tests).</summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (downloadEnabled)
        {
            for (var i = 0; i < 100 && await downloads.RunOnceAsync(cancellationToken); i++)
            {
            }
        }

        var now = _time.GetUtcNow();
        if (retentionEnabled && now >= _nextRetention)
        {
            await retention.RunOnceAsync(cancellationToken); // purge, then drain
            _nextRetention = now.AddMinutes(options.RetentionIntervalMinutes);
        }
        else
        {
            await retention.DrainAsync(null, cancellationToken);
        }

        if (sweeper is not null && now >= _nextSweep)
        {
            _nextSweep = now.AddHours(options.OrphanSweepIntervalHours);
            await sweeper.SweepAsync(now.AddHours(-options.OrphanGraceHours), cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("A media job cycle failed; it will be tried again ({ErrorType}).", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(options.DownloadIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
