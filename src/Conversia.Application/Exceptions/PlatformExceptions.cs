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
        TenantStatus.Paused => "en pausa",
        TenantStatus.Cancelled => "cancelada",
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

/// <summary>
/// The company is not Active (pending, paused, suspended or cancelled), so it cannot operate. The API layer maps this to 403 with the code
/// <see cref="Code"/>, the status and, for a suspension, its cause.
/// </summary>
public class TenantNotActiveException : Exception
{
    /// <summary>The stable machine code clients switch on.</summary>
    public const string Code = "tenant_not_active";

    public TenantNotActiveException(TenantStatus status, SuspensionCause? cause = null)
        : base(MessageFor(status, cause))
    {
        Status = status;
        Cause = status == TenantStatus.Suspended ? cause : null;
    }

    public TenantStatus Status { get; }

    public SuspensionCause? Cause { get; }

    public static string MessageFor(TenantStatus status, SuspensionCause? cause = null) => status switch
    {
        TenantStatus.Suspended when cause == SuspensionCause.Billing => "Tu empresa está suspendida por un pago pendiente.",
        TenantStatus.Suspended => "Tu empresa está suspendida.",
        TenantStatus.Paused => "Tu empresa está en pausa.",
        TenantStatus.Cancelled => "El servicio de tu empresa fue cancelado.",
        _ => "Tu empresa está pendiente de activación.",
    };
}

/// <summary>A lifecycle change the company's status does not allow now. The API layer maps this to 409 with <see cref="Code"/>.</summary>
public class TenantLifecycleConflictException : Exception
{
    /// <summary>The deletion of its data started: it can no longer be reactivated.</summary>
    public const string DeletionStarted = "tenant_deletion_started";

    /// <summary>Only a cancelled company's data can be deleted.</summary>
    public const string NotCancelled = "tenant_not_cancelled";

    /// <summary>The company is cancelled: nothing new can be given to it (a licence, for example).</summary>
    public const string Cancelled = "tenant_cancelled";

    public TenantLifecycleConflictException(string code)
        : base(MessageOf(code))
    {
        Code = code;
    }

    public string Code { get; }

    private static string MessageOf(string code) => code switch
    {
        DeletionStarted => Domain.Entities.Tenant.DeletionStartedMessage,
        NotCancelled => "Solo se pueden eliminar los datos de una empresa cancelada. Cancélala primero.",
        Cancelled => "La empresa está cancelada: no se le puede asignar nada. Reactívala primero.",
        _ => "La empresa no admite ese cambio ahora.",
    };
}

/// <summary>The company of a signed-in user no longer exists (its data was deleted). The API layer maps this to 401 with <see cref="Code"/>.</summary>
public class TenantGoneException : Exception
{
    public const string Code = "tenant_gone";

    public TenantGoneException()
        : base("Tu empresa ya no existe en Conversia: sus datos fueron eliminados al terminar el servicio.")
    {
    }
}
