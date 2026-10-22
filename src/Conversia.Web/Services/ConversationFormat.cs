using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Messages;

namespace Conversia.Web.Services;

/// <summary>How the conversation pages write a customer, an age and a wait. A customer's number is shown whole only where the person has chosen to open the conversation.</summary>
public static class ConversationFormat
{
    /// <summary>The last four digits behind an ellipsis ("…1234"): enough to tell two customers apart in a list, never the number.</summary>
    public static string MaskedPhone(string? waId) => WhatsAppIdMask.Mask(waId);

    /// <summary>The name WhatsApp gave when there is one, else "Cliente" with the masked number.</summary>
    public static string ListTitle(ContactSummary contact)
        => string.IsNullOrWhiteSpace(contact.DisplayName) ? $"Cliente {MaskedPhone(contact.WaId)}" : contact.DisplayName.Trim();

    /// <summary>"hace 5 min", "hace 3 h", "hace 2 d": one unit, the biggest that fits; an instant in the future reads as "ahora".</summary>
    public static string Age(DateTime thenUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - thenUtc;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "ahora";
        }

        return elapsed.TotalHours < 1
            ? $"hace {(int)elapsed.TotalMinutes} min"
            : elapsed.TotalDays < 1
                ? $"hace {(int)elapsed.TotalHours} h"
                : $"hace {(int)elapsed.TotalDays} d";
    }

    /// <summary>The wait for a first reply when there is one, in minutes; null when the customer never wrote or nobody replied.</summary>
    public static double? FirstResponseMinutes(ConversationListItem conversation)
        => conversation.FirstInboundAt is { } inbound && conversation.FirstResponseAt is { } response
            ? Math.Max(0, (response - inbound).TotalMinutes)
            : null;

    /// <summary>Who changed the stage, as the API reports it: an id only. Eight characters are enough to tell users apart in a screen.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    public static string Confidence(decimal? confidence)
        => confidence is { } value ? $"{Math.Round(value * 100, 0, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} %" : MetricsFormat.NoData;

    public static string MessageType(string? type) => MessageTypeLabels.For(type);
}
