using Conversia.Application.Interfaces;
using Conversia.Application.Privacy;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Contacts;

/// <summary>
/// Deletes one contact and everything of it (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"), in two phases. Raw SQL that names the
/// company in every statement; the foreign keys are <c>RESTRICT</c>, so the order is children first.
/// <list type="number">
/// <item>One short transaction under the lock that serializes the company's ingestion (docs/WEBHOOKS.md, "Lock order") and the contact row:
/// the conversations, messages, opportunities and AI usage rows of the contact go, the mark is written and the contact goes. From the commit on,
/// ingestion drops the number's old messages, so nothing can bring it back.</item>
/// <item>Without the ingestion lock, the finished raw deliveries (<c>Processed</c> and <c>Failed</c>) that carry the number are found and
/// deleted in batches of <see cref="ScanBatchSize"/> rows, each with its own transaction and time limit: a company with years of deliveries
/// never holds up its ingestion during the scan. A delivery still <c>Pending</c> is left to the worker, which deletes it right after
/// processing it (it then carries messages the mark drops).</item>
/// </list>
/// </summary>
public sealed class ContactSuppressionStore(ApplicationDbContext db, PrivacyHasher hasher) : IContactSuppressionStore, IContactSuppressionRepository
{
    public const int ScanBatchSize = 500;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed record ContactRow(string WaId);

    public async Task<ContactSuppressionCounts?> SuppressAsync(Guid tenantId, Guid contactId, Guid userId, DateTime now, CancellationToken cancellationToken = default)
    {
        var deleted = await DeleteContactAsync(tenantId, contactId, userId, now, cancellationToken);
        if (deleted is null)
        {
            return null;
        }

        var events = await DeleteFinishedDeliveriesAsync(tenantId, deleted.Value.WaId, cancellationToken);
        return deleted.Value.Counts with { WebhookEvents = events };
    }

    private async Task<(string WaId, ContactSuppressionCounts Counts)?> DeleteContactAsync(
        Guid tenantId, Guid contactId, Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TransactionTimeouts.SetLocalAsync(db, Timeout, Timeout, cancellationToken);
        await TenantIngestionLock.AcquireAsync(db, tenantId, cancellationToken);

        var contact = await db.Database.SqlQuery<ContactRow>($"""
            SELECT "WaId" FROM "Contacts" WHERE "Id" = {contactId} AND "TenantId" = {tenantId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (contact.Count == 0)
        {
            return null;
        }

        var waId = contact[0].WaId;
        var conversations = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "Conversations" WHERE "TenantId" = {tenantId} AND "ContactId" = {contactId}
            """).ToListAsync(cancellationToken);

        // The stored media copies go with the messages: queued in the same transaction, deleted from the store by the media job (docs/MESSAGING.md, "Media").
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MediaObjectDeletions" ("Id", "TenantId", "ObjectKey", "CreatedAt")
            SELECT gen_random_uuid(), "TenantId", "MediaObjectKey", {now} FROM "Messages"
            WHERE "TenantId" = {tenantId} AND "ConversationId" = ANY({conversations}) AND "MediaObjectKey" IS NOT NULL
            """, cancellationToken);
        var messages = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Messages" WHERE "TenantId" = {tenantId} AND "ConversationId" = ANY({conversations})""", cancellationToken);
        var opportunities = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Opportunities" WHERE "TenantId" = {tenantId} AND "ConversationId" = ANY({conversations})""", cancellationToken);
        // The assistant's state of those conversations (docs/ASSISTANT.md).
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "ConversationAssistants" WHERE "TenantId" = {tenantId} AND "ConversationId" = ANY({conversations})""", cancellationToken);
        // Design D24: the AI distinct conversation sets go with the conversations (the monthly totals hold counts only and stay).
        var usage = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "AiUsageConversations" WHERE "TenantId" = {tenantId} AND "ConversationId" = ANY({conversations})""", cancellationToken);
        var deletedConversations = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Conversations" WHERE "TenantId" = {tenantId} AND "ContactId" = {contactId}""", cancellationToken);

        var mark = await FindMarkAsync(tenantId, waId, cancellationToken);
        var current = hasher.ContactSuppression(tenantId, waId);
        if (mark is null)
        {
            db.ContactSuppressions.Add(ContactSuppression.Create(tenantId, current.Hex, current.KeyId, userId, now));
        }
        else
        {
            mark.Renew(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "Contacts" WHERE "Id" = {contactId} AND "TenantId" = {tenantId}""", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (waId, new ContactSuppressionCounts(deletedConversations, messages, opportunities, 0, usage));
    }

    // A keyset over the company's finished deliveries (by id: every row visited once, whatever arrives meanwhile). The number is matched
    // as a whole JSON string value. Each batch re-checks the status, so a delivery that changed meanwhile is left alone.
    private async Task<int> DeleteFinishedDeliveriesAsync(Guid tenantId, string waId, CancellationToken cancellationToken)
    {
        var pattern = "%\"" + EscapeLike(waId) + "\"%";
        var after = Guid.Empty;
        var deleted = 0;
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await TransactionTimeouts.SetLocalAsync(db, Timeout, Timeout, cancellationToken);
            var ids = await db.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM "WebhookEvents"
                WHERE "TenantId" = {tenantId} AND "Status" IN ('Processed', 'Failed') AND "Id" > {after}
                ORDER BY "Id"
                LIMIT {ScanBatchSize}
                """).ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return deleted;
            }

            deleted += await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "WebhookEvents"
                WHERE "TenantId" = {tenantId} AND "Id" = ANY({ids}) AND "Status" IN ('Processed', 'Failed')
                  AND "RawPayload"::text LIKE {pattern} ESCAPE '\'
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            after = ids[^1];
        }
    }

    public async Task<DateTime?> GetSuppressedUntilAsync(Guid tenantId, string waId, CancellationToken cancellationToken = default)
        => (await FindMarkAsync(tenantId, waId, cancellationToken, tracked: false))?.SuppressedAt;

    // A mark made with a retired key is still recognised: the lookup accepts the hash under every known key, each with its own key id.
    private async Task<ContactSuppression?> FindMarkAsync(Guid tenantId, string waId, CancellationToken cancellationToken, bool tracked = true)
    {
        var candidates = hasher.ContactSuppressionCandidates(tenantId, waId).Select(c => c.Hex).ToList();
        var query = db.ContactSuppressions.Where(s => s.TenantId == tenantId && candidates.Contains(s.WaIdHash));
        var marks = await (tracked ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
        return marks.Count == 0 ? null : marks.MaxBy(m => m.SuppressedAt);
    }

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
