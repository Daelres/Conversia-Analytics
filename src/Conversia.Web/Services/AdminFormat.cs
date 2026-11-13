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

    /// <summary>"12,50 USD": money of the platform's own reports (estimates, never a bill).</summary>
    public static string Usd(decimal amount) => $"{amount.ToString("N2", Conversia.Domain.Common.SpanishCulture.Info)} USD";

    /// <summary>A small cost with the precision a single call needs: "0,0042 USD".</summary>
    public static string SmallUsd(decimal amount) => $"{amount.ToString("N4", Conversia.Domain.Common.SpanishCulture.Info)} USD";

    /// <summary>"3 min", "2 h 5 min", or "Sin datos" for a wait that does not exist.</summary>
    public static string Age(long? seconds)
    {
        if (seconds is not { } total)
        {
            return MetricsFormat.NoData;
        }

        if (total < 60)
        {
            return $"{total} s";
        }

        var minutes = total / 60;
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>"Por defecto" or "Propio": whether a limit is the deployment's or the company's own.</summary>
    public static string Origin(bool isDefault) => isDefault ? "Por defecto" : "Propio de la empresa";

    /// <summary>When the AI reads a company, in words (<c>Cada noche desde las 01:00</c>, <c>Cada 6 horas</c>, <c>Casi en tiempo real</c>).</summary>
    public static string AiSchedule(string mode, int? nightlyHour, int? intervalHours) => mode switch
    {
        "Nightly" => nightlyHour is { } hour ? $"Cada noche desde las {hour:00}:00" : "Cada noche",
        "EveryHours" => intervalHours is 1 ? "Cada hora" : $"Cada {intervalHours} horas",
        "NearRealTime" => "Casi en tiempo real",
        _ => mode,
    };

    /// <summary>A count with its word in singular or plural (<c>1 fallida</c>, <c>3 fallidas</c>).</summary>
    public static string Count(int count, string one, string many) => $"{MetricsFormat.Whole(count)} {(count == 1 ? one : many)}";

    /// <summary>The Spanish word of a licence status.</summary>
    public static string LicenseStatus(string status) => status switch
    {
        "Available" => "Disponible",
        "Assigned" => "Asignada",
        "Retired" => "Retirada",
        _ => status,
    };

    /// <summary>The Spanish word of the AI usage state of a company.</summary>
    public static string AiState(string state) => state switch
    {
        "Disabled" => "Desactivada",
        "Ok" => "Dentro del cupo",
        "Warning" => "Cerca del cupo",
        "Exhausted" => "Cupo agotado",
        _ => state,
    };

    /// <summary>The Spanish word of a health status.</summary>
    public static string Health(string status) => status switch
    {
        "Healthy" => "Funciona",
        "Degraded" => "Degradado",
        "Unhealthy" => "Falla",
        _ => status,
    };
}
