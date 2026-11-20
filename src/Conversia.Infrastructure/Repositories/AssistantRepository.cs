using Conversia.Application.Assistant;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Conversia.Infrastructure.Repositories;

/// <summary>The assistant's rows of the caller's company (tenant-filtered). See <see cref="IAssistantRepository"/>.</summary>
public sealed class AssistantRepository(ApplicationDbContext db) : IAssistantRepository
{
    public Task<AssistantSettings?> GetSettingsAsync(CancellationToken cancellationToken = default)
        => db.AssistantSettings.FirstOrDefaultAsync(cancellationToken);

    public void AddSettings(AssistantSettings settings) => db.AssistantSettings.Add(settings);

    public async Task<ConversationAssistant?> FindStateForUpdateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // Added or changed earlier in this transaction (a second signal of the same delivery): the tracked copy is the current one.
        var tracked = db.ConversationAssistants.Local.FirstOrDefault(a => a.ConversationId == conversationId);
        if (tracked is not null && db.Entry(tracked).State is EntityState.Added or EntityState.Modified)
        {
            return tracked;
        }

        // The tenant filter applies to the composed query; FOR UPDATE keeps the job's claim and this change from interleaving.
        var rows = await db.ConversationAssistants
            .FromSql($"""SELECT * FROM "ConversationAssistants" WHERE "ConversationId" = {conversationId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault();
        if (row is not null && ReferenceEquals(row, tracked))
        {
            await db.Entry(row).ReloadAsync(cancellationToken); // an older read of this scope: the locked row is the truth
        }

        return row;
    }

    public Task<ConversationAssistant?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => db.ConversationAssistants.AsNoTracking().FirstOrDefaultAsync(a => a.ConversationId == conversationId, cancellationToken);

    public void AddState(ConversationAssistant state) => db.ConversationAssistants.Add(state);

    public async Task<IReadOnlyList<AssistantKnowledgeVersion>> ListKnowledgeAsync(int take, CancellationToken cancellationToken = default)
        => await db.AssistantKnowledgeVersions.AsNoTracking().OrderByDescending(v => v.Number).Take(take).ToListAsync(cancellationToken);

    public Task<AssistantKnowledgeVersion?> GetKnowledgeAsync(int number, CancellationToken cancellationToken = default)
        => db.AssistantKnowledgeVersions.FirstOrDefaultAsync(v => v.Number == number, cancellationToken);

    public Task<AssistantKnowledgeVersion?> GetPublishedKnowledgeAsync(CancellationToken cancellationToken = default)
        => db.AssistantKnowledgeVersions.FirstOrDefaultAsync(v => v.Status == AssistantKnowledgeStatus.Published, cancellationToken);

    public async Task<AssistantKnowledgeVersion> AddKnowledgeAsync(Guid tenantId, string content, Guid? savedBy, CancellationToken cancellationToken = default)
    {
        // Two saves at the same moment pick the same next number: the unique (company, number) index refuses the second, which tries again.
        for (var attempt = 0; ; attempt++)
        {
            var next = (await db.AssistantKnowledgeVersions.MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
            var version = AssistantKnowledgeVersion.Create(tenantId, next, content, savedBy);
            db.AssistantKnowledgeVersions.Add(version);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return version;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && attempt < 3)
            {
                db.Entry(version).State = EntityState.Detached;
            }
        }
    }

    public async Task<AssistantKnowledgeVersion?> PublishKnowledgeAsync(int number, Guid? userId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var version = await db.AssistantKnowledgeVersions.FirstOrDefaultAsync(v => v.Number == number, cancellationToken);
        if (version is null)
        {
            return null;
        }

        if (version.Status != AssistantKnowledgeStatus.Published)
        {
            var current = await db.AssistantKnowledgeVersions.FirstOrDefaultAsync(v => v.Status == AssistantKnowledgeStatus.Published, cancellationToken);
            if (current is not null)
            {
                current.Archive();
                await db.SaveChangesAsync(cancellationToken); // the archive first: the partial unique index allows one published row
            }

            version.Publish(userId, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConcurrencyConflictException(ex);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    public Task CountAsync(Guid tenantId, bool isSandbox, string metric, DateOnly day, CancellationToken cancellationToken = default)
        => AssistantCounters.AddAsync(db, tenantId, isSandbox, metric, day, cancellationToken);

    public void Audit(AssistantAuditEntry entry) => db.AssistantAuditEntries.Add(entry);

    public async Task<IReadOnlyList<AssistantAuditEntry>> ListAuditAsync(int take, CancellationToken cancellationToken = default)
        => await db.AssistantAuditEntries.AsNoTracking().OrderByDescending(e => e.At).Take(take).ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>The atomic upsert of an assistant counter, in whatever transaction the context is in.</summary>
internal static class AssistantCounters
{
    public static async Task AddAsync(ApplicationDbContext db, Guid tenantId, bool isSandbox, string metric, DateOnly day, CancellationToken cancellationToken)
    {
        var code = metric.Length > AssistantActivityCount.MetricMaxLength ? metric[..AssistantActivityCount.MetricMaxLength] : metric;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AssistantActivityCounts" ("TenantId", "Day", "IsSandbox", "Metric", "Count") VALUES ({tenantId}, {day}, {isSandbox}, {code}, 1)
            ON CONFLICT ("TenantId", "Day", "IsSandbox", "Metric") DO UPDATE SET "Count" = "AssistantActivityCounts"."Count" + 1
            """, cancellationToken);
    }
}

/// <summary>The platform switches, read on every fired timer; a change is audited in the same transaction.</summary>
public sealed class PlatformSwitchStore(ApplicationDbContext db, TimeProvider time) : IPlatformSwitchStore
{
    public async Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken = default)
        => await db.PlatformSwitches.AsNoTracking().Where(s => s.Name == name).Select(s => (bool?)s.Enabled).FirstOrDefaultAsync(cancellationToken)
           ?? PlatformSwitch.DefaultOf(name);

    public async Task<bool> SetAsync(string name, bool enabled, Guid adminId, string reason, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var row = await db.PlatformSwitches.FromSql($"""SELECT * FROM "PlatformSwitches" WHERE "Name" = {name} FOR UPDATE""").FirstOrDefaultAsync(cancellationToken);
        var previous = row?.Enabled ?? PlatformSwitch.DefaultOf(name);
        if (previous == enabled)
        {
            return false;
        }

        if (row is null)
        {
            db.PlatformSwitches.Add(PlatformSwitch.Create(name, enabled, adminId, now));
        }
        else
        {
            row.Set(enabled, adminId, now);
        }

        db.PlatformAuditLogs.Add(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.PlatformAssistantChanged, null, new Dictionary<string, object?>
        {
            ["switch"] = name,
            ["from"] = previous,
            ["to"] = enabled,
            ["reason"] = reason,
        }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConcurrencyConflictException(ex); // two administrators created the row at once
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
