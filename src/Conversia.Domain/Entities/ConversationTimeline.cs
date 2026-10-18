using Conversia.Domain.Enums;

namespace Conversia.Domain.Entities;

/// <summary>
/// The time markers of a conversation, computed from the times its messages carry and never from the order they arrived in
/// (webhooks can be delivered late or out of order).
/// </summary>
/// <param name="StartedAt">The earliest message of any direction.</param>
/// <param name="LastMessageAt">The latest message of any direction.</param>
/// <param name="FirstInboundAt">The earliest message from the customer.</param>
/// <param name="FirstResponseAt">
/// The earliest message from the business at or after <paramref name="FirstInboundAt"/> (timestamps have a resolution of one second, so
/// a same-second reply counts); null while the customer has not written yet or nobody has answered. A business message sent before
/// the customer's first one is not a response.
/// </param>
public sealed record ConversationTimeline(DateTime StartedAt, DateTime LastMessageAt, DateTime? FirstInboundAt, DateTime? FirstResponseAt)
{
    /// <summary>The timeline of the given messages, or null when there are none.</summary>
    public static ConversationTimeline? From(IEnumerable<(MessageDirection Direction, DateTime SentAt)> messages)
    {
        var all = messages.ToList();
        if (all.Count == 0)
        {
            return null;
        }

        DateTime? firstInbound = all.Any(m => m.Direction == MessageDirection.Inbound)
            ? all.Where(m => m.Direction == MessageDirection.Inbound).Min(m => m.SentAt)
            : null;

        DateTime? firstResponse = null;
        if (firstInbound is not null)
        {
            var answers = all.Where(m => m.Direction == MessageDirection.Outbound && m.SentAt >= firstInbound).ToList();
            if (answers.Count > 0)
            {
                firstResponse = answers.Min(m => m.SentAt);
            }
        }

        return new ConversationTimeline(all.Min(m => m.SentAt), all.Max(m => m.SentAt), firstInbound, firstResponse);
    }
}
