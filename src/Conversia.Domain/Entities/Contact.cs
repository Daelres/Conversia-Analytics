using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>A customer, identified inside the tenant by the digits of their WhatsApp id.</summary>
public class Contact : BaseEntity
{
    public const int DisplayNameMaxLength = 200;
    public const int MinWaIdLength = 5;
    public const int MaxWaIdLength = 20;

    public static readonly string WaIdRuleMessage =
        $"El identificador de WhatsApp debe tener entre {MinWaIdLength} y {MaxWaIdLength} dígitos.";

    public static readonly string DisplayNameRuleMessage = $"El nombre del contacto {TextRules.RuleText(DisplayNameMaxLength, required: false)}.";

    public Guid TenantId { get; private set; }

    /// <summary>The WhatsApp id with digits only (no '+', spaces or separators).</summary>
    public string WaId { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public DateTime FirstSeenAt { get; private set; }

    // Required by EF Core for materialization.
    private Contact()
    {
    }

    public static Contact Create(Guid tenantId, string waId, string? displayName, DateTime firstSeenAt)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            WaId = NormalizeWaId(waId) ?? throw new DomainException(WaIdRuleMessage),
            DisplayName = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage),
            FirstSeenAt = UtcTime.Normalize(firstSeenAt),
        };

    /// <summary>Keeps the latest non-blank name the customer's profile showed.</summary>
    public void UpdateDisplayName(string? displayName)
    {
        var name = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage);
        if (name is not null)
        {
            DisplayName = name;
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

    /// <summary>The digits of <paramref name="waId"/> when they form a plausible id (5 to 20 digits), otherwise null.</summary>
    public static string? NormalizeWaId(string? waId)
    {
        if (string.IsNullOrWhiteSpace(waId))
        {
            return null;
        }

        var digits = new string(waId.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= MinWaIdLength and <= MaxWaIdLength ? digits : null;
    }
}
