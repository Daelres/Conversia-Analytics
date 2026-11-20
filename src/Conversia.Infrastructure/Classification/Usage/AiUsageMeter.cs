using Conversia.Domain.Entities;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification.Usage;

/// <summary>
/// The metered <see cref="IAiUsageMeter"/> (design D12, D13, D18; docs/CLASSIFICATION.md, "Usage and quota").
/// <list type="bullet">
/// <item><b>Admission</b>: one read (no lock, no write) of the company's switch and quota, its billable usage of the month and the whole
/// deployment's estimated USD of the month, decided by <see cref="AiAdmissionPolicy"/>.</item>
/// <item><b>Record</b>: in a scope and a short transaction of its own (so the classification's rollback never loses it): the month row of the
/// company (real or sandbox) is created if missing and locked (<c>FOR UPDATE</c>), the conversation is inserted into the month's distinct set
/// (<c>ON CONFLICT DO NOTHING</c>) and the count grows only when it was new; calls, tokens and USD always grow. A failed call adds tokens and
/// USD only. The month is the America/Bogota month at the time of the increment.</item>
/// </list>
/// Across companies on purpose: the deployment cap sums every company's month, and the worker that records has no company set on its scope.
/// The quota is the company's own, else its plan's included conversations, else the default, plus the paid AI add-ons valid now
/// (docs/BILLING.md, "AI add-ons"). Never text: ids, counts, tokens and money.
/// </summary>
public sealed class AiUsageMeter(IServiceScopeFactory scopeFactory, AiQuotaSettings settings, TimeProvider timeProvider, ILogger<AiUsageMeter> logger) : IAiUsageMeter, IAiUsageLossCounter
{
    /// <summary>The event of a usage record that could not be written: alert on it (the cost happened and is only in the log).</summary>
    public static readonly EventId UsageNotRecorded = new(4101, "AiUsageNotRecorded");

    /// <summary>How long a usage record may take: it runs whatever the caller's budget (the money is already spent).</summary>
    public static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(3);

    private long _lost;
    private long _lastLostTicks;

    public long LostRecords => Interlocked.Read(ref _lost);

    public DateTime? LastLostAt => Interlocked.Read(ref _lastLostTicks) is var ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    // The month the cap was last reported for (DateOnly.DayNumber), so the warning is one line per month per instance, not one per conversation.
    private int _capReportedFor = -1;

    public async Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, bool isSandbox, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var month = settings.MonthOf(utcNow);

        var facts = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.AiEnabled,
                Own = t.AiMonthlyConversationQuota,
                Plan = db.BillingSubscriptions.IgnoreQueryFilters().Where(s => s.TenantId == t.Id && s.Status != Domain.Enums.SubscriptionStatus.Cancelled).Join(db.BillingPlans, s => s.PlanId, p => p.Id, (s, p) => (int?)p.IncludedAiConversations).FirstOrDefault(),
                Extra = db.AiQuotaGrants.IgnoreQueryFilters().Where(g => g.TenantId == t.Id && g.ValidFrom <= utcNow && g.ValidUntil > utcNow).Sum(g => (int?)g.Conversations),
                Used = db.AiUsageMonths.IgnoreQueryFilters().Where(u => u.TenantId == t.Id && u.Month == month && !u.IsSandbox).Select(u => (int?)u.ConversationsAnalyzed).FirstOrDefault(),
                Usd = db.AiUsageMonths.IgnoreQueryFilters().Where(u => u.TenantId == t.Id && u.Month == month && !u.IsSandbox).Select(u => (decimal?)u.EstimatedUsd).FirstOrDefault(),
                SandboxUsd = db.AiUsageMonths.IgnoreQueryFilters().Where(u => u.TenantId == t.Id && u.Month == month && u.IsSandbox).Select(u => (decimal?)u.EstimatedUsd).FirstOrDefault(),
                // The deployment cap is shared with the AI sales assistant (docs/ASSISTANT.md, "Costs").
                Deployment = (decimal?)((db.AiUsageMonths.IgnoreQueryFilters().Where(u => u.Month == month).Sum(u => (decimal?)u.EstimatedUsd) ?? 0m)
                    + (db.AssistantUsageMonths.IgnoreQueryFilters().Where(u => u.Month == month).Sum(u => (decimal?)u.EstimatedUsd) ?? 0m)),
                // Batched requests not resolved yet are RESERVED (review: a batch must not overshoot by its size): counted as analysed
                // conversations and at their estimated cost, for the company and for the deployment, until each is collected.
                ReservedConversations = db.LlmBatchRequests.IgnoreQueryFilters()
                    .Count(r => r.TenantId == t.Id && !r.IsSandbox && (r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting)),
                ReservedUsd = db.LlmBatchRequests.IgnoreQueryFilters()
                    .Where(r => r.TenantId == t.Id && !r.IsSandbox && (r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting))
                    .Sum(r => (decimal?)r.EstimatedUsd),
                ReservedSandboxUsd = db.LlmBatchRequests.IgnoreQueryFilters()
                    .Where(r => r.TenantId == t.Id && r.IsSandbox && (r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting))
                    .Sum(r => (decimal?)r.EstimatedUsd),
                ReservedDeployment = db.LlmBatchRequests.IgnoreQueryFilters()
                    .Where(r => r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting)
                    .Sum(r => (decimal?)r.EstimatedUsd),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (facts is null)
        {
            return AiAdmission.AiDisabled;
        }

        var quota = settings.EffectiveQuota(facts.Own ?? facts.Plan) + (facts.Extra ?? 0);
        var admission = AiAdmissionPolicy.Decide(new AiAdmissionFacts(
            facts.AiEnabled, isSandbox, quota, (facts.Used ?? 0) + facts.ReservedConversations, (facts.Usd ?? 0m) + (facts.ReservedUsd ?? 0m), settings.CeilingUsd(quota),
            (facts.Deployment ?? 0m) + (facts.ReservedDeployment ?? 0m), settings.DeploymentMonthlyUsdCap, (facts.SandboxUsd ?? 0m) + (facts.ReservedSandboxUsd ?? 0m),
            settings.SandboxMonthlyUsdPerCompany));
        if (admission == AiAdmission.DeploymentCapReached && Interlocked.Exchange(ref _capReportedFor, month.DayNumber) != month.DayNumber)
        {
            logger.LogWarning(
                "The deployment's monthly AI cap ({CapUsd} USD) is reached for {Month}: every company is read by the rules until the month ends.",
                settings.DeploymentMonthlyUsdCap, month);
        }

        return admission;
    }

    public Task RecordSuccessAsync(AiUsageRecord record, CancellationToken cancellationToken = default) => RecordAsync(record, succeeded: true, cancellationToken);

    public Task RecordFailureAsync(AiUsageRecord record, CancellationToken cancellationToken = default) => RecordAsync(record, succeeded: false, cancellationToken);

    // The caller's token is not used: once the provider answered, the cost exists, so it is written even if the caller's budget ran out
    // (the caller honours its own cancellation afterwards). A record has its own short limit instead.
    private async Task RecordAsync(AiUsageRecord record, bool succeeded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = cancellationToken;
        using var limit = new CancellationTokenSource(RecordTimeout);
        try
        {
            await WriteAsync(record, succeeded, limit.Token);
        }
        catch (Exception ex)
        {
            // The call happened and the classification goes on; the usage is only in this line (ids, tokens, USD) and in the lost counter
            // the administrator's monitoring shows. Alert on the event id.
            Interlocked.Increment(ref _lost);
            Interlocked.Exchange(ref _lastLostTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
            logger.LogError(
                UsageNotRecorded,
                "The AI usage of conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}, succeeded {Succeeded}, tokens in {InputTokens} out {OutputTokens} cache read {CacheReadTokens} cache write {CacheWriteTokens}, estimated USD {EstimatedUsd}) could not be recorded: {Error}",
                record.ConversationId, record.TenantId, record.IsSandbox, succeeded, record.Usage.InputTokens, record.Usage.OutputTokens,
                record.Usage.CacheReadTokens, record.Usage.CacheWriteTokens, record.EstimatedUsd, WebhookErrors.Describe(ex));
        }
    }

    private async Task WriteAsync(AiUsageRecord record, bool succeeded, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);
        var month = settings.MonthOf(now);
        var usage = record.Usage;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AiUsageMonths" ("TenantId", "Month", "IsSandbox", "ConversationsAnalyzed", "Calls", "FailedCalls", "InputTokens", "OutputTokens",
                "CacheReadTokens", "CacheWriteTokens", "EstimatedUsd", "WonDisagreements", "UpdatedAt")
            VALUES ({record.TenantId}, {month}, {record.IsSandbox}, 0, 0, 0, 0, 0, 0, 0, 0, 0, {now})
            ON CONFLICT ("TenantId", "Month", "IsSandbox") DO NOTHING
            """, cancellationToken);
        var locked = await db.AiUsageMonths
            .FromSql($"""
                SELECT * FROM "AiUsageMonths" WHERE "TenantId" = {record.TenantId} AND "Month" = {month} AND "IsSandbox" = {record.IsSandbox} FOR UPDATE
                """)
            .IgnoreQueryFilters().AsNoTracking()
            .ToListAsync(cancellationToken);
        var row = locked.Single(); // created just above if it was missing

        var added = 0;
        if (succeeded)
        {
            added = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiUsageConversations" ("TenantId", "Month", "ConversationId") VALUES ({record.TenantId}, {month}, {record.ConversationId})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
        }

        var analyzed = row.ConversationsAnalyzed + added;
        var usd = row.EstimatedUsd + record.EstimatedUsd;
        DateTime? exhaustedAt = row.ExhaustedAt;
        DateTime? ceilingAt = row.UsdCeilingReachedAt;
        if (!record.IsSandbox && (exhaustedAt is null || ceilingAt is null))
        {
            var own = await db.Tenants.AsNoTracking().Where(t => t.Id == record.TenantId)
                .Select(t => new { Own = t.AiMonthlyConversationQuota, Plan = db.BillingSubscriptions.IgnoreQueryFilters().Where(s => s.TenantId == t.Id && s.Status != Domain.Enums.SubscriptionStatus.Cancelled).Join(db.BillingPlans, s => s.PlanId, p => p.Id, (s, p) => (int?)p.IncludedAiConversations).FirstOrDefault(), Extra = db.AiQuotaGrants.IgnoreQueryFilters().Where(g => g.TenantId == t.Id && g.ValidFrom <= now && g.ValidUntil > now).Sum(g => (int?)g.Conversations) })
                .FirstOrDefaultAsync(cancellationToken);
            var quota = settings.EffectiveQuota(own?.Own ?? own?.Plan) + (own?.Extra ?? 0);
            if (exhaustedAt is null && analyzed >= quota)
            {
                exhaustedAt = now;
                logger.LogWarning(
                    "Tenant {TenantId} used its AI quota for {Month} ({Analyzed} of {Quota} conversations): the rules read its conversations until the month ends.",
                    record.TenantId, month, analyzed, quota);
            }

            if (ceilingAt is null && usd >= settings.CeilingUsd(quota))
            {
                ceilingAt = now;
                logger.LogWarning(
                    "Tenant {TenantId} reached its AI USD ceiling for {Month} ({Usd} of {CeilingUsd} USD): the rules read its conversations until the month ends.",
                    record.TenantId, month, usd, settings.CeilingUsd(quota));
            }
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "AiUsageMonths" SET
                "ConversationsAnalyzed" = "ConversationsAnalyzed" + {added},
                "Calls" = "Calls" + 1,
                "FailedCalls" = "FailedCalls" + {(succeeded ? 0 : 1)},
                "InputTokens" = "InputTokens" + {usage.InputTokens},
                "OutputTokens" = "OutputTokens" + {usage.OutputTokens},
                "CacheReadTokens" = "CacheReadTokens" + {usage.CacheReadTokens},
                "CacheWriteTokens" = "CacheWriteTokens" + {usage.CacheWriteTokens},
                "EstimatedUsd" = "EstimatedUsd" + {record.EstimatedUsd},
                "WonDisagreements" = "WonDisagreements" + {(record.WonDisagreement ? 1 : 0)},
                "ExhaustedAt" = {exhaustedAt},
                "UsdCeilingReachedAt" = {ceilingAt},
                "UpdatedAt" = {now}
            WHERE "TenantId" = {record.TenantId} AND "Month" = {month} AND "IsSandbox" = {record.IsSandbox}
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the distinct conversation sets older than the previous month (design D13, D24): only the current and the previous month are
    /// kept. The month rows (counts, tokens, USD) stay. Returns the rows deleted.
    /// </summary>
    public async Task<int> PurgeConversationSetsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var keepFrom = AiQuotaSettings.PreviousMonth(settings.MonthOf(timeProvider.GetUtcNow().UtcDateTime));
        return await db.AiUsageConversations.IgnoreQueryFilters().Where(c => c.Month < keepFrom).ExecuteDeleteAsync(cancellationToken);
    }
}
