using System.Globalization;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.Interfaces;
using Conversia.Application.Messages;
using Conversia.Application.Time;
using Conversia.Domain.Enums;

namespace Conversia.Application.Services;

public class MessageFeedService : IMessageFeedService
{
    private readonly IMessageFeedQueries _queries;
    private readonly TenantTimeZone _timeZone;
    private readonly TimeProvider _timeProvider;

    public MessageFeedService(IMessageFeedQueries queries, TenantTimeZone timeZone, TimeProvider timeProvider)
    {
        _queries = queries;
        _timeZone = timeZone;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResponse<RecentMessageItem>> RecentAsync(RecentMessagesQuery query, CancellationToken cancellationToken = default)
    {
        var filter = new RecentMessagesFilter(
            query.Page, query.PageSize, query.ChannelId, Parse<MessageDirection>(query.Direction), query.Since?.UtcDateTime, query.AfterId);
        var (items, total) = await _queries.RecentAsync(filter, cancellationToken);
        return new PagedResponse<RecentMessageItem> { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };
    }

    public async Task<MessagesSummaryResponse> SummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var today = _timeZone.LocalDate(now);
        var data = await _queries.SummaryAsync(_timeZone.StartOfDayUtc(today), now, cancellationToken);
        return new MessagesSummaryResponse
        {
            MessagesToday = data.MessagesToday,
            Today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeZone = _timeZone.Id,
            LastInboundAt = data.LastInboundAt,
            LastOutboundAt = data.LastOutboundAt,
            InboundLast24Hours = data.InboundLast24Hours,
            OutboundLast24Hours = data.OutboundLast24Hours,
            Lines = data.Lines,
        };
    }

    public async Task<PagedResponse<WebhookEventItem>> WebhookEventsAsync(WebhookEventsQuery query, CancellationToken cancellationToken = default)
    {
        var filter = new WebhookEventsFilter(query.Page, query.PageSize, query.ChannelId, Parse<WebhookEventStatus>(query.Status));
        var (items, total) = await _queries.WebhookEventsAsync(filter, cancellationToken);
        foreach (var item in items)
        {
            (item.OutcomeCode, item.Outcome) = WebhookOutcomes.Describe(item);
        }

        return new PagedResponse<WebhookEventItem> { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };
    }

    private static T? Parse<T>(string? name)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(name) ? null : Enum.Parse<T>(name.Trim(), ignoreCase: true);
}
