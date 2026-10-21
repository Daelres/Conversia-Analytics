using Conversia.Domain.Enums;

namespace Conversia.Application.Exceptions;

/// <summary>The Spanish words for a company's status, as the messages say them.</summary>
public static class TenantStatusTexts
{
    public static string Label(TenantStatus status) => status switch
    {
        TenantStatus.Pending => "pendiente de activación",
        TenantStatus.Active => "activa",
        TenantStatus.Suspended => "suspendida",
        _ => status.ToString(),
    };
}

/// <summary>The company cannot go from its status to the requested one. The API layer maps this to 409.</summary>
public class TenantStatusConflictException : Exception
{
    public TenantStatusConflictException(TenantStatus from, TenantStatus to)
        : base($"No se puede pasar la empresa de «{TenantStatusTexts.Label(from)}» a «{TenantStatusTexts.Label(to)}».")
    {
    }
}

/// <summary>The quota would end below the lines already switched on. The API layer maps this to 409.</summary>
public class LineQuotaBelowActiveException : Exception
{
    public LineQuotaBelowActiveException(int activeLines)
        : base($"No se puede bajar el cupo por debajo de las líneas activas: la empresa tiene {activeLines} activa(s). Desactiva líneas primero.")
    {
    }
}

/// <summary>The company has all the active lines its agreement allows. The API layer maps this to 409.</summary>
public class LineQuotaFullException : Exception
{
    public LineQuotaFullException(int activeLines, int maxActiveLines)
        : base($"Tu cupo de líneas activas está completo ({activeLines} de {maxActiveLines}). Contacta al administrador de Conversia.")
    {
    }
}

/// <summary>The company is pending or suspended, so it cannot operate. The API layer maps this to 403 with the code <see cref="Code"/>.</summary>
public class TenantNotActiveException : Exception
{
    /// <summary>The stable machine code clients switch on.</summary>
    public const string Code = "tenant_not_active";

    public TenantNotActiveException(TenantStatus status)
        : base(MessageFor(status))
    {
        Status = status;
    }

    public TenantStatus Status { get; }

    public static string MessageFor(TenantStatus status)
        => status == TenantStatus.Suspended ? "Tu empresa está suspendida." : "Tu empresa está pendiente de activación.";
}
