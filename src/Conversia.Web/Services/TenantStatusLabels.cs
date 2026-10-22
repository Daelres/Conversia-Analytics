using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>One thing the administrator can do to a company's status, with the words of its confirmation step.</summary>
/// <param name="Route">The path segment of its confirmation page (<c>/admin/empresas/{id}/estado/{route}</c>).</param>
/// <param name="Danger">Stops something (suspending): its button looks like the other stopping actions.</param>
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
        "La empresa vuelve a operar con sus datos y sus líneas activas. Los mensajes enviados mientras estuvo suspendida no se guardaron.", Danger: false);

    private static readonly StatusAction[] All = [Activate, Suspend, Reactivate];

    public static string Label(TenantStatus status) => status switch
    {
        TenantStatus.Pending => "Pendiente",
        TenantStatus.Active => "Activa",
        TenantStatus.Suspended => "Suspendida",
        _ => status.ToString(),
    };

    /// <summary>The label for the name a status has in the API ("Pending"); the name itself when it is not one.</summary>
    public static string Label(string? status)
        => Enum.TryParse<TenantStatus>(status, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? Label(parsed) : status ?? MetricsFormat.NoData;

    /// <summary>The actions that make sense for a company in this status, in the order they are offered. Suspending a company that is only pending is allowed.</summary>
    public static IReadOnlyList<StatusAction> ActionsFor(TenantStatus current)
        => All.Where(action => Tenant.CanChangeStatus(current, action.Target) && Fits(current, action)).ToList();

    /// <summary>The action with this path segment, or null.</summary>
    public static StatusAction? Find(string? route) => All.FirstOrDefault(a => a.Route == route);

    // "Reactivar" is the way out of Suspended; "Activar" the way out of Pending. The same target would otherwise offer both.
    private static bool Fits(TenantStatus current, StatusAction action)
        => action.Target != TenantStatus.Active || (current == TenantStatus.Suspended) == (action.Route == Reactivate.Route);
}
