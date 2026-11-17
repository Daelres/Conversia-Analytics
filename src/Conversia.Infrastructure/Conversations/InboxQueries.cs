using System.Globalization;
using Conversia.Application.DTOs.Inbox;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Conversations;

/// <summary>
/// The inbox (docs/MESSAGING.md, "Inbox"): bounded queries straight into the list's shape, each on its own index (see the comments), with
/// the names as scalar subqueries, so the cost does not grow with the rows (no N+1).
/// </summary>
public sealed class InboxQueries(ApplicationDbContext db, ICurrentTenantService currentTenant) : IInboxQueries
{
    public async Task<IReadOnlyList<InboxItem>> ListAsync(InboxFilter filter, int take, CancellationToken cancellationToken = default)
    {
        var mode = db.Conversations.AsNoTracking().Where(c => c.IsSandbox == filter.SandboxMode);

        if (filter.ChangedSince is { } since)
        {
            // The delta: every conversation of the company (and mode) changed after the cursor, open or not, whatever its advisor, branch or
            // reply state, so a client can also drop the ones that left its filters. On IX_Conversations_Inbox_Changes, newest change first.
            var changed = await Project(mode.Where(c => c.UpdatedAt > since).OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id).Take(take))
                .ToListAsync(cancellationToken);
            return [.. changed.Select(r => ToItem(r, Matches(r, filter)))];
        }

        var open = Filtered(mode.Where(c => c.Status == ConversationStatus.Open), filter);

        // Two bounded reads instead of sorting every open conversation: the ones awaiting a reply by the customer's last message, oldest
        // first (IX_Conversations_Inbox_Waiting), then the rest by their last message, newest first (IX_Conversations_Inbox_Open).
        var waiting = await Project(open
                .Where(c => c.LastInboundAt != null && (c.LastOutboundAt == null || c.LastOutboundAt < c.LastInboundAt))
                .OrderBy(c => c.LastInboundAt).ThenBy(c => c.Id)
                .Take(take))
            .ToListAsync(cancellationToken);
        if (filter.AwaitingReplyOnly || waiting.Count >= take)
        {
            return [.. waiting.Select(r => ToItem(r, true))];
        }

        var rest = await Project(open
                .Where(c => !(c.LastInboundAt != null && (c.LastOutboundAt == null || c.LastOutboundAt < c.LastInboundAt)))
                .OrderByDescending(c => c.LastMessageAt).ThenBy(c => c.Id)
                .Take(take - waiting.Count))
            .ToListAsync(cancellationToken);
        return [.. waiting.Concat(rest).Select(r => ToItem(r, true))];
    }

    public async Task<InboxChangeToken> GetChangeTokenAsync(bool sandboxMode, CancellationToken cancellationToken = default)
    {
        // Raw SQL names the company itself (the query filters do not apply to it): the last change on IX_Conversations_Inbox_Changes (one index
        // probe) and the open count on IX_Conversations_Inbox_Open, with the database's clock.
        var tenantId = currentTenant.TenantId;
        var row = await db.Database.SqlQuery<TokenRow>($"""
            SELECT (SELECT max("UpdatedAt") FROM "Conversations" WHERE "TenantId" = {tenantId} AND "IsSandbox" = {sandboxMode}) AS "LastChange",
                   (SELECT count(*)::int FROM "Conversations" WHERE "TenantId" = {tenantId} AND "IsSandbox" = {sandboxMode} AND "Status" = 'Open') AS "OpenCount",
                   now() AS "DatabaseNow"
            """).SingleAsync(cancellationToken);
        return new InboxChangeToken(
            row.LastChange is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null, row.OpenCount, DateTime.SpecifyKind(row.DatabaseNow, DateTimeKind.Utc));
    }

    private sealed class TokenRow
    {
        public DateTime? LastChange { get; init; }

        public int OpenCount { get; init; }

        public DateTime DatabaseNow { get; init; }
    }

    private static IQueryable<Domain.Entities.Conversation> Filtered(IQueryable<Domain.Entities.Conversation> query, InboxFilter filter)
    {
        if (filter.AdvisorId is { } advisorId)
        {
            query = query.Where(c => c.AdvisorId == advisorId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(c => c.BranchId == branchId);
        }

        if (filter.Unassigned)
        {
            query = query.Where(c => c.AdvisorId == null);
        }

        return query;
    }

    private static bool Matches(Row r, InboxFilter filter)
        => r.Status == ConversationStatus.Open
           && (filter.AdvisorId is not { } advisor || r.AdvisorId == advisor)
           && (filter.BranchId is not { } branch || r.BranchId == branch)
           && (!filter.Unassigned || r.AdvisorId is null)
           && (!filter.AwaitingReplyOnly || AwaitingReply(r));

    private static bool AwaitingReply(Row r) => r.LastInboundAt is { } inbound && (r.LastOutboundAt is not { } outbound || outbound < inbound);

    private sealed class Row
    {
        public Guid Id { get; init; }

        public string? ContactName { get; init; }

        public string? ContactWaId { get; init; }

        public Guid BranchId { get; init; }

        public string? BranchName { get; init; }

        public Guid? AdvisorId { get; init; }

        public string? AdvisorName { get; init; }

        public ConversationStatus Status { get; init; }

        public DateTime LastMessageAt { get; init; }

        public DateTime? LastInboundAt { get; init; }

        public DateTime? LastOutboundAt { get; init; }

        public bool IsSandbox { get; init; }

        public DateTime ChangedAt { get; init; }
    }

    // The names are scalar subqueries, so a page costs one query however many rows it has (no N+1).
    private IQueryable<Row> Project(IQueryable<Domain.Entities.Conversation> query)
        => query.Select(c => new Row
        {
            Id = c.Id,
            ContactName = db.Contacts.Where(ct => ct.Id == c.ContactId).Select(ct => ct.DisplayName).FirstOrDefault(),
            ContactWaId = db.Contacts.Where(ct => ct.Id == c.ContactId).Select(ct => ct.WaId).FirstOrDefault(),
            BranchId = c.BranchId,
            BranchName = db.Branches.Where(b => b.Id == c.BranchId).Select(b => b.Name).FirstOrDefault(),
            AdvisorId = c.AdvisorId,
            AdvisorName = db.Advisors.Where(a => a.Id == c.AdvisorId).Select(a => a.Name).FirstOrDefault(),
            Status = c.Status,
            LastMessageAt = c.LastMessageAt,
            LastInboundAt = c.LastInboundAt,
            LastOutboundAt = c.LastOutboundAt,
            IsSandbox = c.IsSandbox,
            ChangedAt = c.UpdatedAt ?? c.CreatedAt,
        });

    private static InboxItem ToItem(Row r, bool inFilter) => new()
    {
        Id = r.Id,
        ContactName = r.ContactName,
        ContactWaId = r.ContactWaId ?? string.Empty,
        BranchId = r.BranchId,
        BranchName = r.BranchName,
        AdvisorId = r.AdvisorId,
        AdvisorName = r.AdvisorName,
        Status = r.Status.ToString(),
        LastMessageAt = r.LastMessageAt,
        LastInboundAt = r.LastInboundAt,
        AwaitingReply = AwaitingReply(r),
        IsSandbox = r.IsSandbox,
        ChangedAt = r.ChangedAt,
        InFilter = inFilter,
    };

    public async Task<ConversationVersionResponse?> GetVersionAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var row = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new
            {
                c.Status,
                c.LastMessageAt,
                c.LastInboundAt,
                ChangedAt = c.UpdatedAt ?? c.CreatedAt,
                MessageCount = db.Messages.Count(m => m.ConversationId == c.Id),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new ConversationVersionResponse
        {
            MessageCount = row.MessageCount,
            LastMessageAt = row.LastMessageAt,
            LastInboundAt = row.LastInboundAt,
            Status = row.Status.ToString(),
            Version = string.Create(CultureInfo.InvariantCulture, $"{row.MessageCount}-{row.ChangedAt.Ticks}-{row.Status}"),
        };
    }
}
