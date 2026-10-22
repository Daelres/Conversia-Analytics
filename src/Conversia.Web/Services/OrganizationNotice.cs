namespace Conversia.Web.Services;

/// <summary>Outcome of an organization write, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed back through the query string.</summary>
public static class OrganizationNotice
{
    public const string BranchCreated = "branch";
    public const string AdvisorCreated = "advisor";
    public const string ChannelActivated = "channel-on";
    public const string ChannelDeactivated = "channel-off";

    public const string Path = "/organizacion";

    public static string Url(string code) => $"{Path}?notice={code}";

    public static string? Describe(string? code) => code switch
    {
        BranchCreated => "Sucursal creada.",
        AdvisorCreated => "Asesor creado.",
        ChannelActivated => "Línea activada: ya recibe mensajes.",
        ChannelDeactivated => "Línea desactivada: dejó de recibir mensajes.",
        _ => null,
    };
}
