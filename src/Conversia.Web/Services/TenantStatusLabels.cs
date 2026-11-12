using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>One thing the administrator can do to a company's status, with the words of its confirmation step.</summary>
/// <param name="Route">The path segment of its confirmation page (<c>/admin/empresas/{id}/estado/{route}</c>).</param>
/// <param name="Danger">Stops something (pausing, suspending, cancelling): its button looks like the other stopping actions.</param>
public sealed record StatusAction(string Route, string Button, TenantStatus Target, string Title, string Question, string Effect, bool Danger);

/// <summary>The Spanish words for a company's status and the actions its status allows. The rules of which move is allowed are the Domain's own.</summary>
public static class TenantStatusLabels
{
    public static readonly StatusAction Activate = new(
        "activar", "Activar", TenantStatus.Active, "Activar empresa", "¿Activar esta empresa?",
        "La empresa podrá operar: sus personas verán el panel y podrá activar líneas hasta su cupo. Revisa que el cupo de líneas sea el del acuerdo comercial.", Danger: false);

    public static readonly StatusAction Suspend = new(
        "suspender", "Suspender", TenantStatus.Suspended, "Suspender empresa", "¿Suspender esta empresa?",
        "Sus personas dejan de poder trabajar en el acto y sus líneas dejan de recibir mensajes (el proveedor recibe un rechazo). Los datos se conservan y se retoman al reactivarla.", Danger: true);

    public static readonly StatusAction Reactivate = new(
        "reactivar", "Reactivar", TenantStatus.Active, "Reactivar empresa", "¿Reactivar esta empresa?",
        "La empresa vuelve a operar con sus datos. Si estaba suspendida por el equipo, los mensajes de ese periodo no se guardaron; si estaba suspendida por un pago, se guardaron. Si estaba cancelada, la eliminación de sus datos se anula y sus líneas siguen desactivadas hasta que la empresa las active (asígnale de nuevo sus licencias).", Danger: false);

    public static readonly StatusAction Pause = new(
        "pausar", "Pausar", TenantStatus.Paused, "Pausar empresa", "¿Pausar esta empresa?",
        "Es una pausa acordada: sus personas dejan de poder trabajar en el acto y no se analiza ni se envía nada, pero sus líneas siguen recibiendo y guardando los mensajes, sin perder ninguno. Al reanudarla, el análisis se pone al día.", Danger: true);

    public static readonly StatusAction Resume = new(
        "reanudar", "Reanudar", TenantStatus.Active, "Reanudar empresa", "¿Reanudar esta empresa?",
        "La empresa vuelve a operar y se analizan los mensajes que llegaron durante la pausa.", Danger: false);

    public static readonly StatusAction Cancel = new(
        "cancelar", "Cancelar servicio", TenantStatus.Cancelled, "Cancelar el servicio de la empresa", "¿Cancelar el servicio de esta empresa?",
        "Sus personas dejan de poder trabajar, sus líneas se desactivan y dejan de recibir mensajes, y sus licencias de 360dialog vuelven al inventario. Sus datos se conservan durante el periodo de retención y luego se eliminan para siempre. Puedes reactivarla antes de esa fecha.", Danger: true);

    private static readonly StatusAction[] All = [Activate, Resume, Reactivate, Pause, Suspend, Cancel];

    public static string Label(TenantStatus status) => status switch
    {
        TenantStatus.Pending => "Pendiente",
        TenantStatus.Active => "Activa",
        TenantStatus.Suspended => "Suspendida",
        TenantStatus.Paused => "En pausa",
        TenantStatus.Cancelled => "Cancelada",
        _ => status.ToString(),
    };

    /// <summary>Why a suspended company is suspended, in words; null for any other status.</summary>
    public static string? CauseLabel(TenantStatus status, SuspensionCause? cause) => status != TenantStatus.Suspended ? null : cause switch
    {
        SuspensionCause.Billing => "Pago pendiente (suspensión automática del cobro)",
        _ => "Decisión del equipo de Conversia",
    };

    /// <summary>The label for the name a status has in the API ("Pending"); the name itself when it is not one.</summary>
    public static string Label(string? status)
        => Enum.TryParse<TenantStatus>(status, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? Label(parsed) : status ?? MetricsFormat.NoData;

    /// <summary>The actions that make sense for a company in this status, in the order they are offered. Suspending a company that is only pending is allowed.</summary>
    public static IReadOnlyList<StatusAction> ActionsFor(TenantStatus current)
        => All.Where(action => Tenant.CanChangeStatus(current, action.Target) && Fits(current, action)).ToList();

    /// <summary>Like <see cref="ActionsFor(TenantStatus)"/>, but a company whose data deletion started has none left.</summary>
    public static IReadOnlyList<StatusAction> ActionsFor(TenantSummaryResponse company)
        => company.DataDeletionStartedAt is null ? ActionsFor(company.Status) : [];

    /// <summary>The action with this path segment, or null.</summary>
    public static StatusAction? Find(string? route) => All.FirstOrDefault(a => a.Route == route);

    // Back to Active has a word per status: "Activar" out of Pending, "Reanudar" out of a pause, "Reactivar" out of a suspension or a
    // cancellation. The same target would otherwise offer all three.
    private static bool Fits(TenantStatus current, StatusAction action)
        => action.Target != TenantStatus.Active || action.Route == current switch
        {
            TenantStatus.Pending => Activate.Route,
            TenantStatus.Paused => Resume.Route,
            _ => Reactivate.Route,
        };
}
