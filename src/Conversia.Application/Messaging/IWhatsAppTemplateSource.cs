using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Messaging;

/// <summary>
/// Reads the templates of a channel from its provider (docs/MESSAGING.md, "Templates"), with the channel's own key. Like the sender, it
/// reports the provider's refusals in <see cref="TemplateListResult.Status"/> and never throws for one; no provider type crosses it.
/// </summary>
public interface IWhatsAppTemplateSource
{
    Task<TemplateListResult> ListAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default);
}

/// <param name="Status"><see cref="WhatsAppSendStatus.Sent"/> when every page was read.</param>
/// <param name="Complete">
/// Every entry the provider counted was read and understood (none skipped, the count reached the provider's total): only then may the
/// stored copy lose many templates at once (docs/MESSAGING.md, "Templates").
/// </param>
public sealed record TemplateListResult(WhatsAppSendStatus Status, IReadOnlyList<TemplateSnapshot> Templates, bool Complete = false)
{
    public static TemplateListResult Failed(WhatsAppSendStatus status) => new(status, []);
}

/// <summary>
/// Meta's names for a template's status and category, as the provider's list and its status notifications write them, in any case
/// (docs/MESSAGING.md, "Verified provider facts"). Anything unknown is <see cref="TemplateStatus.Unknown"/>: never offered.
/// </summary>
public static class TemplateNames
{
    /// <summary>The status of a template in the provider's list (<c>approved</c>, <c>PAUSED</c>, ...).</summary>
    public static TemplateStatus StatusOf(string? status) => Normalize(status) switch
    {
        "approved" or "active" or "reinstated" => TemplateStatus.Approved,
        { } s when s.StartsWith("active", StringComparison.Ordinal) => TemplateStatus.Approved, // "Active - High Quality" and the like
        "pending" or "submitted" or "in_review" or "in_appeal" or "appeal_requested" => TemplateStatus.Pending,
        "rejected" => TemplateStatus.Rejected,
        "paused" => TemplateStatus.Paused,
        "disabled" or "deleted" or "archived" or "pending_deletion" => TemplateStatus.Disabled,
        _ => TemplateStatus.Unknown,
    };

    /// <summary>
    /// The status a status notification (<c>message_template_status_update</c>) leaves the template in; null for an event that does not
    /// change whether it can be sent (<c>FLAGGED</c>, <c>LOCKED</c>, <c>UNLOCKED</c>) or that only a sync can settle (<c>UNARCHIVED</c>,
    /// <c>LIMIT_EXCEEDED</c>, an unknown event).
    /// </summary>
    public static TemplateStatus? StatusOfEvent(string? statusEvent) => Normalize(statusEvent) switch
    {
        "approved" or "reinstated" => TemplateStatus.Approved,
        "pending" or "in_appeal" => TemplateStatus.Pending,
        "rejected" => TemplateStatus.Rejected,
        "paused" => TemplateStatus.Paused,
        "disabled" or "archived" or "deleted" or "pending_deletion" => TemplateStatus.Disabled,
        _ => null,
    };

    public static TemplateCategory CategoryOf(string? category) => Normalize(category) switch
    {
        "marketing" => TemplateCategory.Marketing,
        "utility" => TemplateCategory.Utility,
        "authentication" => TemplateCategory.Authentication,
        _ => TemplateCategory.Unknown,
    };

    private static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
        {
            null => null,
            var s => s.Replace("___", "_"), // "active - high quality" -> "active___high_quality" -> "active_high_quality"
        };
}
