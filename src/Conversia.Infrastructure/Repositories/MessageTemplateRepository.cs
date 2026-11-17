using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

/// <summary>The channels' template copies. Tenant-scoped by the query filter; no query repeats a tenant predicate.</summary>
public sealed class MessageTemplateRepository(ApplicationDbContext db) : IMessageTemplateRepository
{
    public Task<MessageTemplate?> FindAsync(Guid channelId, string name, string language, CancellationToken cancellationToken = default)
        => db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.ChannelId == channelId && t.Name == name && t.Language == language, cancellationToken);

    public async Task<IReadOnlyList<MessageTemplate>> ListByChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
        => await db.MessageTemplates.AsNoTracking()
            .Where(t => t.ChannelId == channelId)
            .OrderBy(t => t.Name).ThenBy(t => t.Language)
            .ToListAsync(cancellationToken);

    public async Task<TemplateReplaceResult> ReplaceForChannelAsync(
        Guid tenantId, Guid channelId, IReadOnlyList<TemplateSnapshot> snapshots, bool complete, DateTime syncedAt, DateTime listedAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The channel row is the lock of its templates: a manual sync and the periodic job never interleave their writes.
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "WhatsAppChannels" WHERE "Id" = {channelId} FOR UPDATE""", cancellationToken);

        var stored = await db.MessageTemplates.Where(t => t.ChannelId == channelId).ToListAsync(cancellationToken);
        var byKey = stored.ToDictionary(t => (t.Name, t.Language));

        // The provider should not list a name and language twice; if it does, the last one wins.
        var listed = new Dictionary<(string, string), TemplateSnapshot>();
        foreach (var snapshot in snapshots)
        {
            listed[(snapshot.Name, snapshot.Language)] = snapshot;
        }

        int added = 0, refreshed = 0;
        foreach (var (key, snapshot) in listed)
        {
            if (byKey.TryGetValue(key, out var template))
            {
                template.Refresh(snapshot.Category, snapshot.Status, snapshot.BodyText, snapshot.Supported, syncedAt, listedAt);
                refreshed++;
            }
            else
            {
                db.MessageTemplates.Add(MessageTemplate.Create(
                    tenantId, channelId, snapshot.Name, snapshot.Language, snapshot.Category, snapshot.Status, snapshot.BodyText, snapshot.Supported, syncedAt));
                added++;
            }
        }

        // Two guards against a list that lost templates on the way (docs/MESSAGING.md, "Templates"): an empty list never removes anything,
        // and a list that is not complete (entries skipped, or no total to check against) may remove at most half of the stored copy.
        var gone = stored.Where(t => !listed.ContainsKey((t.Name, t.Language))).ToList();
        var held = 0;
        if (gone.Count > 0 && (listed.Count == 0 || (!complete && gone.Count * 2 > stored.Count)))
        {
            held = gone.Count;
            gone = [];
        }

        db.MessageTemplates.RemoveRange(gone);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TemplateReplaceResult(added, refreshed, gone.Count, held);
    }

    public async Task<bool> ChangeStatusAsync(
        Guid channelId, string name, string language, TemplateStatus status, DateTime changedAt, CancellationToken cancellationToken = default)
    {
        // The channel row is the lock of its templates, as for a sync. The webhook worker calls this inside its own transaction; on its own
        // a transaction is opened here, so the lock is held until the status is written.
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "WhatsAppChannels" WHERE "Id" = {channelId} FOR UPDATE""", cancellationToken);
            var template = await db.MessageTemplates.FirstOrDefaultAsync(t => t.ChannelId == channelId && t.Name == name && t.Language == language, cancellationToken);
            if (template is null || !template.ChangeStatus(status, changedAt))
            {
                return false;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return true;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }
}
