using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Sandbox;

/// <summary>
/// Removes the sandbox data of one company (docs/SANDBOX.md) in one transaction. Raw SQL, which the entities' tenant query filters do
/// not touch: <b>every statement names the tenant itself</b>, and deletes only rows that carry the sandbox mark, or that belong to a
/// conversation that does. The foreign keys are <c>RESTRICT</c>, so the order is children first; if a statement ever matched something a
/// real row still points to, the database refuses it and the whole transaction rolls back.
/// </summary>
public sealed class SandboxDataStore(ApplicationDbContext db) : ISandboxDataStore
{
    /// <summary>Bound of each statement and each lock wait: a clean-up that cannot get its locks fails instead of hanging the request.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<SandboxDataCounts> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TransactionTimeouts.SetLocalAsync(db, Timeout, Timeout, cancellationToken);

        // The lock that serializes everything that adds messages to the tenant's conversations (docs/WEBHOOKS.md, "Lock order"): no
        // delivery of the tenant, simulated or real, is being processed while its sandbox rows go away, and a pending simulated
        // delivery is deleted here before the worker can turn it into new rows.
        await TenantIngestionLock.AcquireAsync(db, tenantId, cancellationToken);

        var messages = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "Messages" m
            USING "Conversations" c
            WHERE m."TenantId" = {tenantId} AND c."Id" = m."ConversationId" AND c."TenantId" = {tenantId} AND c."IsSandbox"
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "Opportunities" o
            USING "Conversations" c
            WHERE o."TenantId" = {tenantId} AND c."Id" = o."ConversationId" AND c."TenantId" = {tenantId} AND c."IsSandbox"
            """, cancellationToken);

        var conversations = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Conversations" WHERE "TenantId" = {tenantId} AND "IsSandbox" """, cancellationToken);

        var contacts = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "Contacts" ct
            WHERE ct."TenantId" = {tenantId} AND ct."IsSandbox"
              AND NOT EXISTS (SELECT 1 FROM "Conversations" c WHERE c."ContactId" = ct."Id")
            """, cancellationToken);

        var events = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "WebhookEvents" WHERE "TenantId" = {tenantId} AND "IsSandbox" """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SandboxDataCounts(conversations, messages, contacts, events);
    }
}
