namespace Conversia.Web.Services;

/// <summary>Outcome of an administrator action, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed back through the query string.</summary>
public static class AdminNotice
{
    public const string StatusChanged = "estado";
    public const string QuotaSaved = "cupo";
    public const string LineOff = "linea";
    public const string SandboxOn = "sandbox-activo";
    public const string SandboxOff = "sandbox-inactivo";

    public static string Url(Guid companyId, string code) => $"/admin/empresas/{companyId}?aviso={code}";

    public static string? Describe(string? code) => code switch
    {
        StatusChanged => "Estado actualizado.",
        QuotaSaved => "Cupo guardado.",
        LineOff => "Línea desactivada: dejó de recibir mensajes.",
        SandboxOn => "Modo sandbox activado: la empresa ya no envía mensajes reales y sus métricas muestran solo datos de prueba.",
        SandboxOff => "Modo sandbox desactivado: la empresa vuelve a operar con datos reales.",
        _ => null,
    };
}
