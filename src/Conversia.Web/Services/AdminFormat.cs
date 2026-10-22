namespace Conversia.Web.Services;

/// <summary>How the administrator's pages write dates: the zone is the one the interface already uses, and "no value" is always said in words.</summary>
public static class AdminFormat
{
    public const string NoActivity = "Sin actividad";

    /// <summary>"18 oct 2026, 14:05" for an instant already converted to the interface's zone.</summary>
    public static string Moment(DateTime local) => $"{MetricsFormat.Date(DateOnly.FromDateTime(local))}, {MetricsFormat.Clock(local)}";

    /// <summary>"2 de 5": the lines on out of the lines the agreement allows.</summary>
    public static string LinesOfQuota(int active, int max) => $"{MetricsFormat.Whole(active)} de {MetricsFormat.Whole(max)}";

    /// <summary>"1 empresa", "3 empresas".</summary>
    public static string Companies(int count) => count == 1 ? "1 empresa" : $"{MetricsFormat.Whole(count)} empresas";

    /// <summary>"1 cambio", "12 cambios".</summary>
    public static string Changes(int count) => count == 1 ? "1 cambio" : $"{MetricsFormat.Whole(count)} cambios";
}
