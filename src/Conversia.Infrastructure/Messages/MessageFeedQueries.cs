using Conversia.Application.DTOs.Messages;
using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Application.Messages;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Messages;

/// <summary>
/// The messages feed, the arrival counters and the webhook deliveries, read straight into the response shapes. Every query is untracked and
/// scoped to the current tenant by the global query filters; none repeats a tenant predicate. The raw payload of a delivery is never selected.
/// </summary>
public sealed class MessageFeedQueries(ApplicationDbContext db) : IMessageFeedQueries
{
    public async Task<(IReadOnlyList<RecentMessageItem> Items, int TotalCount)> RecentAsync(RecentMessagesFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.Messages.AsNoTracking();

        if (filter.ChannelId is { } channelId)
        {
            query = query.Where(m => m.ChannelId == channelId);
        }

        if (filter.Direction is { } direction)
        {
            query = query.Where(m => m.Direction == direction);
        }

        if (filter.SinceUtc is { } since)
        {
            query = query.Where(m => m.SentAt >= since);
        }

        if (filter.AfterId is { } afterId)
        {
            // The reference is a message of this tenant (the filter hides the others): anything else is a 404, like any foreign id.
            var anchor = await db.Messages.AsNoTracking().Where(m => m.Id == afterId).Select(m => new { m.SentAt }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new MessageNotFoundException();
            var anchorTime = anchor.SentAt;
            query = query.Where(m => m.SentAt > anchorTime || (m.SentAt == anchorTime && m.Id.CompareTo(afterId) > 0));
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0 || (long)(filter.Page - 1) * filter.PageSize >= total)
        {
            return ([], total);
        }

        var rows = await (
                from m in query.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
                    .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
                join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                join ct in db.Contacts.AsNoTracking() on c.ContactId equals ct.Id
                join ch in db.WhatsAppChannels.AsNoTracking() on m.ChannelId equals ch.Id
                orderby m.SentAt descending, m.Id descending
                select new
                {
                    Message = m,
                    ct.DisplayName,
                    ct.WaId,
                    ch.DisplayPhoneNumber,
                    BranchName = db.Branches.Where(b => b.Id == ch.BranchId).Select(b => b.Name).FirstOrDefault(),
                })
            .ToListAsync(cancellationToken);

        return (rows.Select(r => new RecentMessageItem
        {
            Id = r.Message.Id,
            ConversationId = r.Message.ConversationId,
            SentAt = r.Message.SentAt,
            ReceivedAt = r.Message.CreatedAt,
            Direction = r.Message.Direction.ToString(),
            Type = r.Message.Type,
            TypeLabel = MessageTypeLabels.For(r.Message.Type),
            Text = r.Message.Text,
            Line = new LineReference { ChannelId = r.Message.ChannelId, DisplayPhoneNumber = r.DisplayPhoneNumber, BranchName = r.BranchName },
            Contact = new MessageContact { DisplayName = r.DisplayName, MaskedWaId = WhatsAppIdMask.Mask(r.WaId) },
        }).ToList(), total);
    }

    public async Task<MessagesSummaryData> SummaryAsync(DateTime startOfTodayUtc, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var since24 = nowUtc.AddHours(-24);
        var messages = db.Messages.AsNoTracking();

        var counts = await messages
            .Where(m => m.SentAt >= startOfTodayUtc || m.SentAt >= since24)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Today = g.Count(m => m.SentAt >= startOfTodayUtc),
                In24 = g.Count(m => m.SentAt >= since24 && m.Direction == MessageDirection.Inbound),
                Out24 = g.Count(m => m.SentAt >= since24 && m.Direction == MessageDirection.Outbound),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var lastInbound = await messages.Where(m => m.Direction == MessageDirection.Inbound).MaxAsync(m => (DateTime?)m.SentAt, cancellationToken);
        var lastOutbound = await messages.Where(m => m.Direction == MessageDirection.Outbound).MaxAsync(m => (DateTime?)m.SentAt, cancellationToken);

        var lines = await db.WhatsAppChannels.AsNoTracking()
            .OrderBy(ch => ch.CreatedAt).ThenBy(ch => ch.Id)
            .Select(ch => new LineActivity
            {
                ChannelId = ch.Id,
                DisplayPhoneNumber = ch.DisplayPhoneNumber,
                IsActive = ch.IsActive,
                BranchName = db.Branches.Where(b => b.Id == ch.BranchId).Select(b => b.Name).FirstOrDefault(),
                LastDeliveryAt = db.WebhookEvents.Where(e => e.ChannelId == ch.Id).Max(e => (DateTime?)e.ReceivedAt),
            })
            .ToListAsync(cancellationToken);

        return new MessagesSummaryData(counts?.Today ?? 0, lastInbound, lastOutbound, counts?.In24 ?? 0, counts?.Out24 ?? 0, lines);
    }

    public async Task<(IReadOnlyList<WebhookEventItem> Items, int TotalCount)> WebhookEventsAsync(WebhookEventsFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.WebhookEvents.AsNoTracking();

        if (filter.ChannelId is { } channelId)
        {
            query = query.Where(e => e.ChannelId == channelId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(e => e.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0 || (long)(filter.Page - 1) * filter.PageSize >= total)
        {
            return ([], total);
        }

        // Only the columns the page shows: the raw payload (customers' numbers and text) is never read.
        var rows = await (
                from e in query.OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
                    .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
                join ch in db.WhatsAppChannels.AsNoTracking() on e.ChannelId equals ch.Id
                orderby e.ReceivedAt descending, e.Id descending
                select new
                {
                    e.Id,
                    e.ChannelId,
                    ch.DisplayPhoneNumber,
                    BranchName = db.Branches.Where(b => b.Id == ch.BranchId).Select(b => b.Name).FirstOrDefault(),
                    e.ReceivedAt,
                    e.Status,
                    e.Attempts,
                    e.ProcessedAt,
                    e.SkippedMessageCount,
                    e.StatusCount,
                    e.StoredMessageCount,
                    e.DuplicateMessageCount,
                    e.LastError,
                })
            .ToListAsync(cancellationToken);

        return (rows.Select(r => new WebhookEventItem
        {
            Id = r.Id,
            Line = new LineReference { ChannelId = r.ChannelId, DisplayPhoneNumber = r.DisplayPhoneNumber, BranchName = r.BranchName },
            ReceivedAt = r.ReceivedAt,
            Status = r.Status.ToString(),
            Attempts = r.Attempts,
            ProcessedAt = r.ProcessedAt,
            SkippedMessageCount = r.SkippedMessageCount,
            StatusCount = r.StatusCount,
            StoredMessageCount = r.StoredMessageCount,
            DuplicateMessageCount = r.DuplicateMessageCount,
            // Stored sanitized already; sanitized again on the way out so that nothing older or hand-written can leak a secret or a number.
            LastError = string.IsNullOrWhiteSpace(r.LastError) ? null : WebhookErrors.SanitizeStored(r.LastError),
        }).ToList(), total);
    }
}
