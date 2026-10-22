using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Domain.Enums;

namespace Conversia.Application.Interfaces;

/// <summary>
/// The "did my message arrive?" side of the product: the newest messages of the caller's company, counters of the arrival and the webhook
/// deliveries received (including the ones that stored nothing). Everything is scoped to the caller's tenant by the query filters.
/// </summary>
public interface IMessageFeedService
{
    /// <summary>Newest first (by <c>sentAt</c>, then id). Throws <see cref="Exceptions.MessageNotFoundException"/> for an unknown or other-tenant <c>AfterId</c>.</summary>
    Task<PagedResponse<RecentMessageItem>> RecentAsync(RecentMessagesQuery query, CancellationToken cancellationToken = default);

    Task<MessagesSummaryResponse> SummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Newest first (by arrival). The raw bodies are never part of the answer.</summary>
    Task<PagedResponse<WebhookEventItem>> WebhookEventsAsync(WebhookEventsQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The recent-messages filter, parsed: the direction is a real enum and the instant is UTC.</summary>
public sealed record RecentMessagesFilter(int Page, int PageSize, Guid? ChannelId = null, MessageDirection? Direction = null, DateTime? SinceUtc = null, Guid? AfterId = null);

public sealed record WebhookEventsFilter(int Page, int PageSize, Guid? ChannelId = null, WebhookEventStatus? Status = null);

/// <summary>The counters of <see cref="MessagesSummaryResponse"/> that come from the database; the service adds the time zone and the clock.</summary>
public sealed record MessagesSummaryData(
    int MessagesToday, DateTime? LastInboundAt, DateTime? LastOutboundAt, int InboundLast24Hours, int OutboundLast24Hours, IReadOnlyList<LineActivity> Lines);

/// <summary>Read side of the feed: projections straight to the response shapes, tenant-scoped by the query filters.</summary>
public interface IMessageFeedQueries
{
    Task<(IReadOnlyList<RecentMessageItem> Items, int TotalCount)> RecentAsync(RecentMessagesFilter filter, CancellationToken cancellationToken = default);

    Task<MessagesSummaryData> SummaryAsync(DateTime startOfTodayUtc, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WebhookEventItem> Items, int TotalCount)> WebhookEventsAsync(WebhookEventsFilter filter, CancellationToken cancellationToken = default);
}
