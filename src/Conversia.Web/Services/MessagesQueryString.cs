using System.Globalization;
using Conversia.Application.DTOs.Messages;

namespace Conversia.Web.Services;

/// <summary>The query strings of <c>GET /api/messages/recent</c> and <c>GET /api/webhook-events</c>, as the API binds them.</summary>
internal static class MessagesQueryString
{
    public static string Build(RecentMessagesQuery query)
    {
        var parts = new List<string>
        {
            $"page={query.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={query.PageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        Add(parts, "channelId", query.ChannelId?.ToString());
        Add(parts, "direction", query.Direction);
        Add(parts, "since", query.Since?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        Add(parts, "afterId", query.AfterId?.ToString());
        return "?" + string.Join('&', parts);
    }

    public static string Build(WebhookEventsQuery query)
    {
        var parts = new List<string>
        {
            $"page={query.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={query.PageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        Add(parts, "channelId", query.ChannelId?.ToString());
        Add(parts, "status", query.Status);
        return "?" + string.Join('&', parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
