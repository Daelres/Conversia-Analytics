using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Messages;

/// <summary>
/// Query string of <c>GET /api/messages/recent</c>: the newest messages of the company, newest first (by <c>sentAt</c>, then id).
/// <c>Since</c> keeps the messages sent at or after an instant; <c>AfterId</c> (the newest message the caller has seen) keeps only the
/// ones that come after it in the same order, which is how a page polls for what is new.
/// </summary>
public class RecentMessagesQuery
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>Only the messages of this line (a WhatsApp number of the company).</summary>
    [Display(Name = "Línea")]
    public Guid? ChannelId { get; set; }

    [Display(Name = "Dirección")]
    [EnumName(typeof(MessageDirection))]
    public string? Direction { get; set; }

    /// <summary>An ISO 8601 instant; messages sent at or after it.</summary>
    [Display(Name = "Desde")]
    public DateTimeOffset? Since { get; set; }

    /// <summary>The id of the newest message the caller has seen; only messages after it are returned. An unknown id is a 404.</summary>
    [Display(Name = "Después de")]
    public Guid? AfterId { get; set; }
}

/// <summary>A WhatsApp number of the company and the branch it belongs to.</summary>
public class LineReference
{
    public Guid ChannelId { get; set; }

    public string DisplayPhoneNumber { get; set; } = string.Empty;

    public string? BranchName { get; set; }
}

/// <summary>The customer of a message: their WhatsApp name and the last four digits of their number, never the whole number.</summary>
public class MessageContact
{
    public string? DisplayName { get; set; }

    /// <summary>"…1234".</summary>
    public string MaskedWaId { get; set; } = string.Empty;
}

public class RecentMessageItem
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>When the message was sent, as the provider reported it.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>When the service stored it: the arrival time. Compare it with <see cref="SentAt"/> to see a delay.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary><c>Inbound</c> (the customer) or <c>Outbound</c> (the business).</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>The provider's type in lower case: <c>text</c>, <c>image</c>...</summary>
    public string Type { get; set; } = string.Empty;

    public string TypeLabel { get; set; } = string.Empty;

    /// <summary>Null when the message has no text (media without caption).</summary>
    public string? Text { get; set; }

    public LineReference Line { get; set; } = new();

    public MessageContact Contact { get; set; } = new();
}

/// <summary>What <c>GET /api/messages/summary</c> says about the arrival of messages, to answer "is it arriving?" at a glance.</summary>
public class MessagesSummaryResponse
{
    /// <summary>Messages sent since local midnight of the company's time zone (<c>Metrics:TimeZone</c>).</summary>
    public int MessagesToday { get; set; }

    /// <summary>The local date <see cref="MessagesToday"/> counts (yyyy-MM-dd).</summary>
    public string Today { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public DateTime? LastInboundAt { get; set; }

    public DateTime? LastOutboundAt { get; set; }

    /// <summary>Messages sent in the last 24 hours.</summary>
    public int InboundLast24Hours { get; set; }

    public int OutboundLast24Hours { get; set; }

    /// <summary>Every line of the company, with the last time a delivery arrived on it.</summary>
    public IReadOnlyList<LineActivity> Lines { get; set; } = [];
}

public class LineActivity : LineReference
{
    public bool IsActive { get; set; }

    /// <summary>When the last webhook delivery arrived on this line; null when none ever did (or the old ones were already purged).</summary>
    public DateTime? LastDeliveryAt { get; set; }
}

/// <summary>Query string of <c>GET /api/webhook-events</c>: the deliveries the webhook received, newest first.</summary>
public class WebhookEventsQuery
{
    public const int MaxPageSize = 100;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 25;

    [Display(Name = "Línea")]
    public Guid? ChannelId { get; set; }

    /// <summary><c>Pending</c>, <c>Processed</c> or <c>Failed</c>.</summary>
    [Display(Name = "Estado")]
    [EnumName(typeof(WebhookEventStatus))]
    public string? Status { get; set; }
}

/// <summary>
/// One webhook delivery as the company may see it. The body of the delivery is never part of it (it holds customers' numbers and content
/// that belongs to the message list): only counts, times and a sanitized error.
/// </summary>
public class WebhookEventItem
{
    public Guid Id { get; set; }

    public LineReference Line { get; set; } = new();

    public DateTime ReceivedAt { get; set; }

    /// <summary><c>Pending</c>, <c>Processed</c> or <c>Failed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTime? ProcessedAt { get; set; }

    /// <summary>Entries that looked like messages but could not be stored; null until processed.</summary>
    public int? SkippedMessageCount { get; set; }

    /// <summary>Delivery receipts (sent, delivered, read) in the delivery; null until processed.</summary>
    public int? StatusCount { get; set; }

    /// <summary>Messages this delivery added; null until processed, and for deliveries processed before this was recorded.</summary>
    public int? StoredMessageCount { get; set; }

    /// <summary>Messages of the delivery already stored by an earlier one.</summary>
    public int? DuplicateMessageCount { get; set; }

    /// <summary>A stable code for <see cref="Outcome"/>: <c>stored</c>, <c>receipts</c>, <c>skipped</c>, <c>repeated</c>, <c>empty</c>, <c>processed</c>, <c>failed</c> or <c>pending</c>.</summary>
    public string OutcomeCode { get; set; } = string.Empty;

    /// <summary>What happened, in Spanish.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The last failure, already sanitized (secrets redacted, long numbers masked); null when there was none. Never the raw error.</summary>
    public string? LastError { get; set; }
}
