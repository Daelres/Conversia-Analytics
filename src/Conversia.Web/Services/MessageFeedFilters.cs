using System.Globalization;
using Conversia.Application.DTOs.Messages;

namespace Conversia.Web.Services;

/// <summary>
/// What the messages page is filtered by and how it refreshes, read from its address. Anything that is not a value the API knows (a
/// malformed id, another direction, a refresh rate that is not on offer) is dropped instead of reaching the API, so a hand-edited address
/// shows the unfiltered page rather than an error.
/// </summary>
public sealed record MessageFeedFilters
{
    public const string Path = "/mensajes";
    public const int PageSize = 25;

    /// <summary>The <c>direccion</c> values.</summary>
    public const string InboundText = "entrantes";

    public const string OutboundText = "salientes";

    /// <summary>The seconds the page may reload itself at; zero is off.</summary>
    public static readonly IReadOnlyList<int> AutoRefreshChoices = [5, 10, 30];

    public Guid? ChannelId { get; init; }

    /// <summary><c>Inbound</c> or <c>Outbound</c>.</summary>
    public string? Direction { get; init; }

    public int Page { get; init; } = 1;

    /// <summary>Seconds between reloads; 0 means the page does not reload itself.</summary>
    public int AutoSeconds { get; init; }

    /// <summary>The newest message the person marked as seen: what arrived after it is flagged as new.</summary>
    public Guid? SeenId { get; init; }

    public static MessageFeedFilters Parse(string? line, string? direction, string? page, string? auto, string? seen)
        => new()
        {
            ChannelId = Guid.TryParse(line, out var channel) && channel != Guid.Empty ? channel : null,
            Direction = direction switch { InboundText => "Inbound", OutboundText => "Outbound", _ => null },
            Page = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : 1,
            AutoSeconds = int.TryParse(auto, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && AutoRefreshChoices.Contains(seconds) ? seconds : 0,
            SeenId = Guid.TryParse(seen, out var id) && id != Guid.Empty ? id : null,
        };

    public bool IsFiltered => ChannelId is not null || Direction is not null;

    public RecentMessagesQuery ToQuery() => new() { Page = Page, PageSize = PageSize, ChannelId = ChannelId, Direction = Direction };

    /// <summary>The messages that arrived after the seen one, with the same filters (at most one API page of them).</summary>
    public RecentMessagesQuery ToNewerQuery(Guid afterId)
        => new() { Page = 1, PageSize = RecentMessagesQuery.MaxPageSize, ChannelId = ChannelId, Direction = Direction, AfterId = afterId };

    /// <summary>The address of the page with these settings; <paramref name="page"/> 1 and "no refresh" carry nothing.</summary>
    public string ToUrl(int? page = null, int? autoSeconds = null, Guid? seenId = null, bool keepSeen = true)
    {
        var parts = new List<string>();
        Add(parts, "linea", ChannelId?.ToString());
        Add(parts, "direccion", Direction switch { "Inbound" => InboundText, "Outbound" => OutboundText, _ => null });
        var pageNumber = page ?? Page;
        if (pageNumber > 1)
        {
            Add(parts, "page", pageNumber.ToString(CultureInfo.InvariantCulture));
        }

        var seconds = autoSeconds ?? AutoSeconds;
        if (seconds > 0)
        {
            Add(parts, "auto", seconds.ToString(CultureInfo.InvariantCulture));
        }

        var seen = seenId ?? (keepSeen ? SeenId : null);
        Add(parts, "visto", seen?.ToString());
        return parts.Count == 0 ? Path : $"{Path}?{string.Join('&', parts)}";
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}

/// <summary>What the deliveries page is filtered by, read from its address; same rules as <see cref="MessageFeedFilters"/>.</summary>
public sealed record WebhookEventFilters
{
    public const string Path = "/mensajes/eventos";
    public const int PageSize = 25;

    public static readonly IReadOnlyList<string> Statuses = ["Pending", "Processed", "Failed"];

    public Guid? ChannelId { get; init; }

    public string? Status { get; init; }

    public int Page { get; init; } = 1;

    public int AutoSeconds { get; init; }

    public static WebhookEventFilters Parse(string? line, string? status, string? page, string? auto)
        => new()
        {
            ChannelId = Guid.TryParse(line, out var channel) && channel != Guid.Empty ? channel : null,
            Status = Statuses.FirstOrDefault(s => string.Equals(s, status, StringComparison.Ordinal)),
            Page = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : 1,
            AutoSeconds = int.TryParse(auto, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && MessageFeedFilters.AutoRefreshChoices.Contains(seconds) ? seconds : 0,
        };

    public bool IsFiltered => ChannelId is not null || Status is not null;

    public WebhookEventsQuery ToQuery() => new() { Page = Page, PageSize = PageSize, ChannelId = ChannelId, Status = Status };

    public string ToUrl(int? page = null, int? autoSeconds = null)
    {
        var parts = new List<string>();
        Add(parts, "linea", ChannelId?.ToString());
        Add(parts, "estado", Status);
        var pageNumber = page ?? Page;
        if (pageNumber > 1)
        {
            Add(parts, "page", pageNumber.ToString(CultureInfo.InvariantCulture));
        }

        var seconds = autoSeconds ?? AutoSeconds;
        if (seconds > 0)
        {
            Add(parts, "auto", seconds.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? Path : $"{Path}?{string.Join('&', parts)}";
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
