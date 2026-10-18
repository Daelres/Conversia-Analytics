namespace Conversia.Web.Services;

/// <summary>
/// What a person reads for the enum names the API sends (<c>Won</c>, <c>Ads</c>, <c>Price</c>). The names stay identifiers (query strings,
/// JSON, <c>data-*</c> hooks); a value added to an enum shows under its own name until it gets a label here.
/// </summary>
public static class ConversationLabels
{
    public static readonly IReadOnlyList<string> Stages = ["New", "InFollowUp", "Won", "Lost"];

    public static readonly IReadOnlyList<string> Sources = ["Ads", "Organic", "Unknown"];

    public static readonly IReadOnlyList<string> Statuses = ["Open", "Closed"];

    /// <summary>The reasons a person may record for a lost sale, in the order they are offered.</summary>
    public static readonly IReadOnlyList<string> LossReasons =
        ["Price", "Availability", "Location", "Timing", "Competitor", "NoResponse", "NotInterested", "Other", "Unknown"];

    public static string Stage(string? stage) => stage switch
    {
        "New" => "Nueva",
        "InFollowUp" => "En seguimiento",
        "Won" => "Ganada",
        "Lost" => "Perdida",
        _ => Fallback(stage),
    };

    public static string Source(string? source) => source switch
    {
        "Ads" => "Anuncios",
        "Organic" => "Orgánico",
        "Unknown" => "Desconocido",
        _ => Fallback(source),
    };

    public static string Status(string? status) => status switch
    {
        "Open" => "Abierta",
        "Closed" => "Cerrada",
        _ => Fallback(status),
    };

    public static string StageSource(string? stageSource) => stageSource switch
    {
        "Auto" => "Automática",
        "Manual" => "Manual",
        _ => Fallback(stageSource),
    };

    public static string LossReason(string? reason) => reason switch
    {
        "Price" => "Precio",
        "Availability" => "Disponibilidad",
        "Location" => "Ubicación",
        "Timing" => "No era el momento",
        "Competitor" => "Eligió a la competencia",
        "NoResponse" => "Dejó de responder",
        "NotInterested" => "No le interesa",
        "Other" => "Otro motivo",
        "Unknown" => "Motivo desconocido",
        _ => Fallback(reason),
    };

    public static string Objection(string? objection) => objection switch
    {
        "None" => "Ninguna",
        "Price" => "Precio",
        "Timing" => "Momento",
        "Location" => "Ubicación",
        "Trust" => "Confianza",
        "Pain" => "Dolor del procedimiento",
        "Other" => "Otra",
        _ => Fallback(objection),
    };

    private static string Fallback(string? name) => string.IsNullOrWhiteSpace(name) ? "—" : name;
}
