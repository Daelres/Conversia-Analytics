using Conversia.Application.Classification.Llm;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// Counts how each manual send ended (<c>sent</c> or the problem code) per company, day (America/Bogota) and sandbox flag, with one atomic
/// upsert in a scope of its own. Counts only. Kept <see cref="RetentionDays"/> days (purged by the usage retention service).
/// </summary>
public sealed class MessageSendOutcomeRecorder(
    IServiceScopeFactory scopeFactory, AiQuotaSettings calendar, TimeProvider timeProvider, ILogger<MessageSendOutcomeRecorder> logger) : IMessageSendOutcomeRecorder
{
    public const int RetentionDays = 90;

    public async Task RecordAsync(Guid tenantId, bool isSandbox, string outcome, CancellationToken cancellationToken = default)
    {
        var code = outcome.Length > MessageSendOutcome.OutcomeMaxLength ? outcome[..MessageSendOutcome.OutcomeMaxLength] : outcome;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var day = calendar.DayOf(timeProvider.GetUtcNow().UtcDateTime);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "MessageSendOutcomes" ("TenantId", "Day", "IsSandbox", "Outcome", "Count") VALUES ({tenantId}, {day}, {isSandbox}, {code}, 1)
                ON CONFLICT ("TenantId", "Day", "IsSandbox", "Outcome") DO UPDATE SET "Count" = "MessageSendOutcomes"."Count" + 1
                """, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The outcome {Outcome} of a send of tenant {TenantId} could not be counted: {Error}", code, tenantId, WebhookErrors.Describe(ex));
        }
    }

    /// <summary>Deletes the counters older than <see cref="RetentionDays"/> days. Returns the rows deleted.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = calendar.DayOf(timeProvider.GetUtcNow().UtcDateTime).AddDays(-RetentionDays);
        return await db.MessageSendOutcomes.IgnoreQueryFilters().Where(o => o.Day < before).ExecuteDeleteAsync(cancellationToken);
    }
}
