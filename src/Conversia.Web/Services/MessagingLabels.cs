namespace Conversia.Web.Services;

/// <summary>What a person reads for the provider names the API sends (<c>Sandbox</c>, <c>Production</c>). The names stay identifiers.</summary>
public static class MessagingLabels
{
    public const string Sandbox = "Sandbox";

    public const string Production = "Production";

    /// <summary>The environments a channel can send through, in the order they are offered.</summary>
    public static readonly IReadOnlyList<string> Environments = [Sandbox, Production];

    public static string Environment(string? environment) => environment switch
    {
        Sandbox => "Sandbox (pruebas)",
        Production => "Producción",
        _ => string.IsNullOrWhiteSpace(environment) ? "—" : environment,
    };
}
