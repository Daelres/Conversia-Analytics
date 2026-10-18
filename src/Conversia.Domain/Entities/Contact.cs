using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>A customer, identified inside the tenant by the digits of their WhatsApp id.</summary>
public class Contact : BaseEntity
{
    public const int DisplayNameMaxLength = 200;
    public const int MinWaIdLength = 7;
    public const int MaxWaIdLength = 15;

    public static readonly string WaIdRuleMessage =
        $"El identificador de WhatsApp debe tener entre {MinWaIdLength} y {MaxWaIdLength} dígitos (con un '+' inicial opcional).";

    public static readonly string DisplayNameRuleMessage = $"El nombre del contacto {TextRules.RuleText(DisplayNameMaxLength, required: false)}.";

    public Guid TenantId { get; private set; }

    /// <summary>The WhatsApp id with digits only (no '+', spaces or separators).</summary>
    public string WaId { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public DateTime FirstSeenAt { get; private set; }

    /// <summary>The time of the message whose profile name <see cref="DisplayName"/> is, so an older delivery cannot overwrite a newer name.</summary>
    public DateTime? DisplayNameAt { get; private set; }

    // Required by EF Core for materialization.
    private Contact()
    {
    }

    public static Contact Create(Guid tenantId, string waId, string? displayName, DateTime firstSeenAt)
    {
        var name = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage);
        var seen = UtcTime.Normalize(firstSeenAt);
        return new Contact
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            WaId = NormalizeWaId(waId) ?? throw new DomainException(WaIdRuleMessage),
            DisplayName = name,
            DisplayNameAt = name is null ? null : seen,
            FirstSeenAt = seen,
        };
    }

    /// <summary>
    /// Keeps the profile name of the latest message: a non-blank name replaces the current one only when it was seen at or after the
    /// message the current one came from (deliveries can be processed out of order).
    /// </summary>
    public void UpdateDisplayName(string? displayName, DateTime observedAt)
    {
        var name = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage);
        var observed = UtcTime.Normalize(observedAt);
        if (name is not null && (DisplayNameAt is null || observed >= DisplayNameAt))
        {
            DisplayName = name;
            DisplayNameAt = observed;
        }
    }

    /// <summary>A message older than the first one seen moves <see cref="FirstSeenAt"/> back (webhooks can arrive out of order).</summary>
    public void ObserveAt(DateTime sentAt)
    {
        var observed = UtcTime.Normalize(sentAt);
        if (observed < FirstSeenAt)
        {
            FirstSeenAt = observed;
        }
    }

    /// <summary>
    /// The WhatsApp id when it is one: after trimming and an optional leading '+', only ASCII digits, 7 to 15 of them (E.164). Nothing
    /// is stripped or repaired: spaces, dashes, dots and letters make it null. In particular a business-scoped user id such as
    /// <c>CO.13491208655302741918</c> is not a phone number and must never be turned into one, or different customers would merge.
    /// </summary>
    public static string? NormalizeWaId(string? waId)
    {
        var id = waId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (id[0] == '+')
        {
            id = id[1..];
        }

        return id.Length is >= MinWaIdLength and <= MaxWaIdLength && id.All(char.IsAsciiDigit) ? id : null;
    }
}
