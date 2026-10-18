using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Conversations;

/// <summary>
/// The conversation list and detail, read straight into the response shapes. Every query is untracked and scoped to the current tenant by
/// the global query filters; none repeats a tenant predicate.
/// </summary>
public sealed class ConversationQueries(ApplicationDbContext db) : IConversationQueries
{
    public async Task<(IReadOnlyList<ConversationListItem> Items, int TotalCount)> ListAsync(ConversationFilter filter, CancellationToken cancellationToken = default)
    {
        var query = Filtered(filter);

        var total = await query.CountAsync(cancellationToken);
        if (total == 0 || (long)(filter.Page - 1) * filter.PageSize >= total)
        {
            return ([], total);
        }

        var rows = await Project(query.OrderByDescending(r => r.Conversation.StartedAt).ThenByDescending(r => r.Conversation.Id))
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (rows.Select(ToItem).ToList(), total);
    }

    public async Task<ConversationListItem?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await Project(Joined().Where(r => r.Conversation.Id == id)).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToItem(row);
    }

    public async Task<ConversationDetailResponse?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await Project(Joined().Where(r => r.Conversation.Id == id)).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.SentAt).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(ConversationDetailResponse.MaxMessages)
            .Select(m => new MessageResponse
            {
                Id = m.Id,
                Direction = m.Direction.ToString(),
                Type = m.Type,
                Text = m.Text,
                SentAt = m.SentAt,
            })
            .ToListAsync(cancellationToken);

        var item = ToItem(row);
        return new ConversationDetailResponse
        {
            Id = item.Id,
            Contact = item.Contact,
            ChannelId = item.ChannelId,
            BranchId = item.BranchId,
            BranchName = item.BranchName,
            AdvisorId = item.AdvisorId,
            AdvisorName = item.AdvisorName,
            AdvisorAssignedBy = row.Conversation.AdvisorAssignedBy,
            AdvisorAssignedAt = row.Conversation.AdvisorAssignedAt,
            Source = item.Source,
            SourceReference = item.SourceReference,
            Status = item.Status,
            StartedAt = item.StartedAt,
            LastMessageAt = item.LastMessageAt,
            FirstInboundAt = item.FirstInboundAt,
            FirstResponseAt = item.FirstResponseAt,
            ClosedAt = item.ClosedAt,
            MessageCount = item.MessageCount,
            Opportunity = item.Opportunity,
            Messages = messages,
        };
    }

    private sealed class Parts
    {
        public required Conversation Conversation { get; init; }

        public required Opportunity Opportunity { get; init; }

        public required Contact Contact { get; init; }
    }

    private sealed class Row
    {
        public required Conversation Conversation { get; init; }

        public required Opportunity Opportunity { get; init; }

        public required Contact Contact { get; init; }

        public string? BranchName { get; init; }

        public string? AdvisorName { get; init; }

        public int MessageCount { get; init; }
    }

    private IQueryable<Parts> Joined()
        => from c in db.Conversations.AsNoTracking()
           join o in db.Opportunities.AsNoTracking() on c.Id equals o.ConversationId
           join ct in db.Contacts.AsNoTracking() on c.ContactId equals ct.Id
           select new Parts { Conversation = c, Opportunity = o, Contact = ct };

    private IQueryable<Parts> Filtered(ConversationFilter f)
    {
        var query = Joined();

        if (f.BranchId is { } branchId)
        {
            query = query.Where(r => r.Conversation.BranchId == branchId);
        }

        if (f.AdvisorId is { } advisorId)
        {
            query = query.Where(r => r.Conversation.AdvisorId == advisorId);
        }

        if (f.Unassigned)
        {
            query = query.Where(r => r.Conversation.AdvisorId == null);
        }

        if (f.Stage is { } stage)
        {
            query = query.Where(r => r.Opportunity.Stage == stage);
        }

        if (f.Source is { } source)
        {
            query = query.Where(r => r.Conversation.Source == source);
        }

        if (f.Status is { } status)
        {
            query = query.Where(r => r.Conversation.Status == status);
        }

        if (f.FromUtc is { } from)
        {
            query = query.Where(r => r.Conversation.StartedAt >= from);
        }

        if (f.ToUtc is { } to)
        {
            query = query.Where(r => r.Conversation.StartedAt < to);
        }

        // One definition of "unanswered" everywhere (docs/METRICS.md): the customer wrote and nobody replied. While the conversation is open
        // somebody still can (the actionable set, the same as the metrics' unansweredOpen); closed, nobody did (unansweredClosed).
        if (f.UnansweredOnly)
        {
            query = query.Where(r => r.Conversation.FirstInboundAt != null && r.Conversation.FirstResponseAt == null && r.Conversation.Status == ConversationStatus.Open);
        }

        if (f.ClosedUnansweredOnly)
        {
            query = query.Where(r => r.Conversation.FirstInboundAt != null && r.Conversation.FirstResponseAt == null && r.Conversation.Status == ConversationStatus.Closed);
        }

        return query;
    }

    // The branch and advisor names and the message count are scalar subqueries, so the page costs one query however many rows it has.
    private IQueryable<Row> Project(IQueryable<Parts> query)
        => query.Select(r => new Row
        {
            Conversation = r.Conversation,
            Opportunity = r.Opportunity,
            Contact = r.Contact,
            BranchName = db.Branches.Where(b => b.Id == r.Conversation.BranchId).Select(b => b.Name).FirstOrDefault(),
            AdvisorName = db.Advisors.Where(a => a.Id == r.Conversation.AdvisorId).Select(a => a.Name).FirstOrDefault(),
            MessageCount = db.Messages.Count(m => m.ConversationId == r.Conversation.Id),
        });

    private static ConversationListItem ToItem(Row row) => new()
    {
        Id = row.Conversation.Id,
        Contact = new ContactSummary { Id = row.Contact.Id, WaId = row.Contact.WaId, DisplayName = row.Contact.DisplayName },
        ChannelId = row.Conversation.ChannelId,
        BranchId = row.Conversation.BranchId,
        BranchName = row.BranchName,
        AdvisorId = row.Conversation.AdvisorId,
        AdvisorName = row.AdvisorName,
        Source = row.Conversation.Source.ToString(),
        SourceReference = row.Conversation.SourceReference,
        Status = row.Conversation.Status.ToString(),
        StartedAt = row.Conversation.StartedAt,
        LastMessageAt = row.Conversation.LastMessageAt,
        FirstInboundAt = row.Conversation.FirstInboundAt,
        FirstResponseAt = row.Conversation.FirstResponseAt,
        ClosedAt = row.Conversation.ClosedAt,
        MessageCount = row.MessageCount,
        Opportunity = OpportunityResponse.From(row.Opportunity),
    };
}
