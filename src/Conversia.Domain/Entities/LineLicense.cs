using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// A WhatsApp line licence of the platform's inventory (docs/PLATFORM_ADMIN.md, "Line licences"): a number contracted with the provider
/// (360dialog), with what it costs the platform per month. Only the platform administrator creates, assigns, releases and retires them. It
/// works whether Conversia resells the number or the customer owns it (<see cref="CustomerOwned"/>, cost 0). Platform-level: the company it is
/// assigned to is data (<see cref="AssignedTenantId"/>), so no tenant query filter applies.
/// </summary>
public class LineLicense : BaseEntity
{
    public const string Dialog360 = "360dialog";
    public const int ProviderMaxLength = 32;
    public const int PhoneDigitsMaxLength = 15;
    public const int NotesMaxLength = 500;
    public const decimal MaxMonthlyCostUsd = 10_000m;
    private const int VisibleDigits = 4;
    private const string MaskPrefix = "•••• ";

    public static readonly string NotesRuleMessage = $"Las notas {TextRules.RuleText(NotesMaxLength, required: false)}.";

    public static readonly string CostRuleMessage = $"El costo mensual debe estar entre 0 y {MaxMonthlyCostUsd:0} USD, con hasta dos decimales.";

    public const string CustomerOwnedCostRuleMessage = "Una licencia del cliente no le cuesta nada a la plataforma: su costo mensual debe ser 0.";

    public string Provider { get; private set; } = Dialog360;

    /// <summary>The digits of the number (7 to 15). Shown to the administrator masked (<see cref="MaskedPhoneNumber"/>).</summary>
    public string PhoneDigits { get; private set; } = string.Empty;

    /// <summary>The provider's id of the channel or number, when known.</summary>
    public string? ProviderChannelId { get; private set; }

    /// <summary>What the licence costs the platform per month, for reporting only (not a bill).</summary>
    public decimal MonthlyCostUsd { get; private set; }

    /// <summary>The number belongs to the customer (it pays the provider itself): the licence is tracked, at cost 0.</summary>
    public bool CustomerOwned { get; private set; }

    public LineLicenseStatus Status { get; private set; } = LineLicenseStatus.Available;

    public Guid? AssignedTenantId { get; private set; }

    /// <summary>The company channel (line) the licence was matched to; null until the company registers or switches on the number.</summary>
    public Guid? ChannelId { get; private set; }

    public DateTime? AssignedAt { get; private set; }

    public DateTime? ReleasedAt { get; private set; }

    public DateTime? RetiredAt { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>): two administrators never both win a change of the same licence.</summary>
    public uint Version { get; private set; }

    public string MaskedPhoneNumber => Mask(PhoneDigits);

    // Required by EF Core for materialization.
    private LineLicense()
    {
    }

    /// <summary>The number as the administrator sees it after creation: only its last four digits.</summary>
    public static string Mask(string digits)
        => MaskPrefix + (digits.Length <= VisibleDigits ? digits : digits[^VisibleDigits..]);

    /// <summary>The digits of a number written in any of the formats a channel accepts; null when it is not a valid number.</summary>
    public static string? DigitsOf(string? phone)
        => WhatsAppChannel.IsValidDisplayPhoneNumber(phone) ? new string([.. phone!.Where(char.IsAsciiDigit)]) : null;

    public static LineLicense Create(string phone, string? providerChannelId, decimal monthlyCostUsd, bool customerOwned, string? notes)
    {
        var digits = DigitsOf(phone) ?? throw new DomainException(WhatsAppChannel.DisplayPhoneNumberRuleMessage);
        if (!WhatsAppChannel.IsValidProviderPhoneNumberId(providerChannelId))
        {
            throw new DomainException(WhatsAppChannel.ProviderPhoneNumberIdRuleMessage);
        }

        if (monthlyCostUsd is < 0m or > MaxMonthlyCostUsd || decimal.Round(monthlyCostUsd, 2) != monthlyCostUsd)
        {
            throw new DomainException(CostRuleMessage);
        }

        if (customerOwned && monthlyCostUsd != 0m)
        {
            throw new DomainException(CustomerOwnedCostRuleMessage);
        }

        return new LineLicense
        {
            PhoneDigits = digits,
            ProviderChannelId = string.IsNullOrWhiteSpace(providerChannelId) ? null : providerChannelId.Trim(),
            MonthlyCostUsd = monthlyCostUsd,
            CustomerOwned = customerOwned,
            Notes = TextRules.Optional(notes, NotesMaxLength, NotesRuleMessage),
        };
    }

    public void AssignTo(Guid tenantId, DateTime now)
    {
        if (Status != LineLicenseStatus.Available)
        {
            throw new DomainException("Solo se puede asignar una licencia disponible.");
        }

        var tenant = TextRules.RequiredId(tenantId, "La empresa es obligatoria.");
        AssignedAt = UtcTime.Normalize(now);
        AssignedTenantId = tenant;
        Status = LineLicenseStatus.Assigned;
        ChannelId = null;
    }

    /// <summary>Back to the inventory: no company, no channel.</summary>
    public void Release(DateTime now)
    {
        if (Status != LineLicenseStatus.Assigned)
        {
            throw new DomainException("Solo se puede liberar una licencia asignada.");
        }

        ReleasedAt = UtcTime.Normalize(now);
        Status = LineLicenseStatus.Available;
        AssignedTenantId = null;
        ChannelId = null;
    }

    public void Retire(DateTime now)
    {
        if (Status != LineLicenseStatus.Available)
        {
            throw new DomainException("Solo se puede retirar una licencia disponible: libérala primero.");
        }

        RetiredAt = UtcTime.Normalize(now);
        Status = LineLicenseStatus.Retired;
    }

    public void LinkChannel(Guid channelId)
    {
        if (Status != LineLicenseStatus.Assigned)
        {
            throw new DomainException("Solo una licencia asignada se vincula a una línea.");
        }

        ChannelId = TextRules.RequiredId(channelId, "La línea es obligatoria.");
    }

    /// <summary>Whether a company line is this licence's number: the same digits, or the same provider id when both have one.</summary>
    public bool Matches(string channelPhone, string? channelProviderId)
        => string.Equals(DigitsOf(channelPhone), PhoneDigits, StringComparison.Ordinal)
           || (ProviderChannelId is not null && !string.IsNullOrWhiteSpace(channelProviderId)
               && string.Equals(channelProviderId.Trim(), ProviderChannelId, StringComparison.Ordinal));
}
