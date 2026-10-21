using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class Tenant : BaseEntity
{
    private const int MaxNameLength = 200;

    public const int MaxLinesLimit = 1000;
    public const int StatusReasonMaxLength = 500;

    public static readonly string StatusReasonRuleMessage = $"El motivo {TextRules.RuleText(StatusReasonMaxLength, required: false)}.";

    public static readonly string MaxActiveLinesRuleMessage = $"El cupo de líneas debe ser un número entero entre 0 y {MaxLinesLimit}.";

    public string Name { get; private set; } = string.Empty;

    /// <summary>Starts <see cref="TenantStatus.Pending"/>: the platform administrator decides when a company operates.</summary>
    public TenantStatus Status { get; private set; } = TenantStatus.Pending;

    /// <summary>How many WhatsApp lines (channels) the company may have switched on at once, per its commercial agreement. 0 at registration.</summary>
    public int MaxActiveLines { get; private set; }

    public DateTime? StatusChangedAt { get; private set; }

    /// <summary>The platform administrator who last changed the status; null while the company is as it registered.</summary>
    public Guid? StatusChangedBy { get; private set; }

    public string? StatusReason { get; private set; }

    // Required by EF Core for materialization.
    private Tenant()
    {
    }

    public static Tenant Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre de la empresa es obligatorio.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new DomainException($"El nombre de la empresa no puede superar los {MaxNameLength} caracteres.");
        }

        if (name.Contains('\0'))
        {
            throw new DomainException("El nombre de la empresa no puede contener caracteres nulos.");
        }

        return new Tenant
        {
            Name = name
        };
    }

    /// <summary>
    /// The transitions an administrator may make: Pending to Active or Suspended, Active to Suspended, Suspended to Active.
    /// Staying where it is, and going back to Pending, are not transitions.
    /// </summary>
    public static bool CanChangeStatus(TenantStatus from, TenantStatus to)
        => (from, to) switch
        {
            (TenantStatus.Pending, TenantStatus.Active) => true,
            (TenantStatus.Pending, TenantStatus.Suspended) => true,
            (TenantStatus.Active, TenantStatus.Suspended) => true,
            (TenantStatus.Suspended, TenantStatus.Active) => true,
            _ => false,
        };

    /// <summary>Moves the company to <paramref name="newStatus"/> on behalf of a platform administrator. Validates before it assigns anything.</summary>
    public void ChangeStatus(TenantStatus newStatus, Guid administratorId, string? reason, DateTime now)
    {
        if (!CanChangeStatus(Status, newStatus))
        {
            throw new DomainException($"No se puede pasar una empresa de {Status} a {newStatus}.");
        }

        var cleanReason = TextRules.Optional(reason, StatusReasonMaxLength, StatusReasonRuleMessage);
        Status = newStatus;
        StatusReason = cleanReason;
        StatusChangedBy = TextRules.RequiredId(administratorId, "El administrador es obligatorio.");
        StatusChangedAt = now;
    }

    /// <summary>Sets the quota of simultaneously active lines. Whether it is below the lines in use is for the caller to check, under the tenant lock.</summary>
    public void SetMaxActiveLines(int maxActiveLines)
    {
        if (maxActiveLines is < 0 or > MaxLinesLimit)
        {
            throw new DomainException(MaxActiveLinesRuleMessage);
        }

        MaxActiveLines = maxActiveLines;
    }
}
