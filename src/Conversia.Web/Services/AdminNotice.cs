namespace Conversia.Web.Services;

/// <summary>Outcome of an administrator action, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed back through the query string.</summary>
public static class AdminNotice
{
    public const string StatusChanged = "estado";
    public const string QuotaSaved = "cupo";
    public const string LineOff = "linea";
    public const string SandboxOn = "sandbox-activo";
    public const string SandboxOff = "sandbox-inactivo";
    public const string AiOn = "ia-activa";
    public const string AiOff = "ia-inactiva";
    public const string AiQuotaSaved = "cupo-ia";
    public const string SendLimitSaved = "limite-envios";
    public const string AiScheduleSaved = "analisis-ia";
    public const string DataDeletionRequested = "eliminacion";

    public static string Url(Guid companyId, string code) => $"/admin/empresas/{companyId}?aviso={code}";

    public static string? Describe(string? code) => code switch
    {
        StatusChanged => "Estado actualizado.",
        QuotaSaved => "Cupo guardado.",
        LineOff => "Línea desactivada: dejó de recibir mensajes.",
        SandboxOn => "Modo sandbox activado: la empresa ya no envía mensajes reales y sus métricas muestran solo datos de prueba.",
        SandboxOff => "Modo sandbox desactivado: la empresa vuelve a operar con datos reales.",
        AiOn => "IA activada: las próximas conversaciones de la empresa se analizan con IA, dentro de su cupo.",
        AiOff => "IA desactivada: las conversaciones de la empresa se clasifican solo con reglas.",
        AiQuotaSaved => "Cupo mensual de IA guardado.",
        SendLimitSaved => "Límite de envíos guardado.",
        AiScheduleSaved => "Frecuencia del análisis con IA guardada: se aplica desde su próxima ventana.",
        DataDeletionRequested => "Eliminación de datos en curso: se hace en segundo plano. Cuando termine, la empresa deja de aparecer y la auditoría lo registra.",
        _ => null,
    };
}

/// <summary>Outcome of a licence action, carried across the post/redirect/get as a fixed code.</summary>
public static class LicenseNotice
{
    public const string Created = "creada";
    public const string Assigned = "asignada";
    public const string Released = "liberada";
    public const string Retired = "retirada";

    public static string Url(Guid licenseId, string code) => $"/admin/licencias/{licenseId}?aviso={code}";

    public static string? Describe(string? code) => code switch
    {
        Created => "Licencia creada: está disponible para asignarla a una empresa.",
        Assigned => "Licencia asignada.",
        Released => "Licencia liberada: volvió al inventario.",
        Retired => "Licencia retirada del inventario.",
        _ => null,
    };
}
