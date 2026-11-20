using Conversia.Application.Assistant;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Assistant;

/// <summary>
/// The assistant's own usage category (docs/ASSISTANT.md, "Costs"): admission before each generation and the record of what it cost, in
/// <c>AssistantUsageMonths</c>, apart from the conversation analysis.
/// <list type="bullet">
/// <item><b>Admission</b> (a read, no lock): the company's AI switch; the deployment's monthly cap (<c>Classification:Quota:DeploymentMonthlyUsdCap</c>),
/// which the analysis and the assistant share; the company's monthly cap of the assistant (<c>Assistant:Llm:TenantMonthlyUsdCap</c>, real
/// conversations) or its sandbox bound (<c>Assistant:Llm:SandboxMonthlyUsdPerCompany</c>). Refused: the job hands off (<c>quota_exhausted</c>).</item>
/// <item><b>Record</b> in a scope and a short transaction of its own (the month row created if missing and locked, counters added); never
/// throws: a record that cannot be written is logged at Error (event 4406, ids, tokens and USD) and counted as lost.</item>
/// </list>
/// Across companies on purpose (the deployment cap). Never text.
/// </summary>
public sealed class AssistantUsageMeter(
    IServiceScopeFactory scopeFactory, AiQuotaSettings quota, AssistantLlmOptions options, TimeProvider time, ILogger<AssistantUsageMeter> logger) : IAssistantAdmission
{
    public static readonly EventId UsageNotRecorded = new(4406, "AssistantUsageNotRecorded");

    private long _lost;

    public long LostRecords => Interlocked.Read(ref _lost);

    public async Task<bool> AdmitAsync(Guid tenantId, bool isSandbox, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var month = quota.MonthOf(time.GetUtcNow().UtcDateTime);
        var enabled = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => (bool?)t.AiEnabled).FirstOrDefaultAsync(cancellationToken);
        if (enabled != true)
        {
            return false;
        }

        var deployment = (await db.AiUsageMonths.IgnoreQueryFilters().Where(u => u.Month == month).SumAsync(u => (decimal?)u.EstimatedUsd, cancellationToken) ?? 0m)
            + (await db.AssistantUsageMonths.IgnoreQueryFilters().Where(u => u.Month == month).SumAsync(u => (decimal?)u.EstimatedUsd, cancellationToken) ?? 0m);
        if (deployment >= quota.DeploymentMonthlyUsdCap)
        {
            return false;
        }

        var spent = await db.AssistantUsageMonths.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.Month == month && u.IsSandbox == isSandbox)
            .Select(u => (decimal?)u.EstimatedUsd).FirstOrDefaultAsync(cancellationToken) ?? 0m;
        return spent < (isSandbox ? options.SandboxMonthlyUsdPerCompany : options.TenantMonthlyUsdCap);
    }

    public async Task RecordUsageAsync(Guid tenantId, Guid conversationId, bool isSandbox, AssistantUsage usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _ = cancellationToken; // the money is spent: the record runs whatever the caller's budget, with its own limit
        using var limit = new CancellationTokenSource(AiUsageMeterLimits.RecordTimeout);
        try
        {
            await WriteAsync(tenantId, isSandbox, usage, limit.Token);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _lost);
            logger.LogError(UsageNotRecorded,
                "The assistant usage of conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}, calls {Calls}, tokens in {InputTokens} out {OutputTokens} cache read {CacheReadTokens} cache write {CacheWriteTokens}, estimated USD {EstimatedUsd}) could not be recorded: {Error}",
                conversationId, tenantId, isSandbox, usage.Calls, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.EstimatedUsd,
                WebhookErrors.Describe(ex));
        }
    }

    private async Task WriteAsync(Guid tenantId, bool isSandbox, AssistantUsage usage, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DatabaseTime.Truncate(time.GetUtcNow().UtcDateTime);
        var month = quota.MonthOf(now);
        var cap = isSandbox ? options.SandboxMonthlyUsdPerCompany : options.TenantMonthlyUsdCap;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AssistantUsageMonths" ("TenantId", "Month", "IsSandbox", "Generations", "Calls", "GuardCalls", "FailedCalls", "InputTokens", "OutputTokens",
                "CacheReadTokens", "CacheWriteTokens", "EstimatedUsd", "UpdatedAt")
            VALUES ({tenantId}, {month}, {isSandbox}, 0, 0, 0, 0, 0, 0, 0, 0, 0, {now})
            ON CONFLICT ("TenantId", "Month", "IsSandbox") DO NOTHING
            """, cancellationToken);
        var row = (await db.AssistantUsageMonths
            .FromSql($"""SELECT * FROM "AssistantUsageMonths" WHERE "TenantId" = {tenantId} AND "Month" = {month} AND "IsSandbox" = {isSandbox} FOR UPDATE""")
            .IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken)).Single();
        var usd = row.EstimatedUsd + usage.EstimatedUsd;
        var capReachedAt = row.CapReachedAt;
        if (capReachedAt is null && usd >= cap)
        {
            capReachedAt = now;
            logger.LogWarning("Tenant {TenantId} reached its monthly assistant cap for {Month} (Sandbox {Sandbox}, {Usd} of {CapUsd} USD): conversations go to a person until the month ends.",
                tenantId, month, isSandbox, usd, cap);
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "AssistantUsageMonths" SET
                "Generations" = "Generations" + 1,
                "Calls" = "Calls" + {usage.Calls},
                "GuardCalls" = "GuardCalls" + {usage.GuardCalls},
                "FailedCalls" = "FailedCalls" + {usage.FailedCalls},
                "InputTokens" = "InputTokens" + {(long)usage.InputTokens},
                "OutputTokens" = "OutputTokens" + {(long)usage.OutputTokens},
                "CacheReadTokens" = "CacheReadTokens" + {usage.CacheReadTokens},
                "CacheWriteTokens" = "CacheWriteTokens" + {usage.CacheWriteTokens},
                "EstimatedUsd" = "EstimatedUsd" + {usage.EstimatedUsd},
                "CapReachedAt" = {capReachedAt},
                "UpdatedAt" = {now}
            WHERE "TenantId" = {tenantId} AND "Month" = {month} AND "IsSandbox" = {isSandbox}
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>The record limit shared with the analysis meter.</summary>
internal static class AiUsageMeterLimits
{
    public static readonly TimeSpan RecordTimeout = Classification.Usage.AiUsageMeter.RecordTimeout;
}
