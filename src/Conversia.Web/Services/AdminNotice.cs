namespace Conversia.Web.Services;

/// <summary>Outcome of an administrator action, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed back through the query string.</summary>
public static class AdminNotice
{
    public const string StatusChanged = "estado";
    public const string QuotaSaved = "cupo";
    public const string LineOff = "linea";

    public static string Url(Guid companyId, string code) => $"/admin/empresas/{companyId}?aviso={code}";

    public static string? Describe(string? code) => code switch
    {
        StatusChanged => "Estado actualizado.",
        QuotaSaved => "Cupo guardado.",
        LineOff => "Línea desactivada: dejó de recibir mensajes.",
        _ => null,
    };
}
