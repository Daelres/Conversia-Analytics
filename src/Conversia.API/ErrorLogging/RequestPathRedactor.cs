using System.Text.RegularExpressions;
using Conversia.Application.DTOs.Organization;

namespace Conversia.API.ErrorLogging;

/// <summary>
/// The channel key in <c>/api/webhooks/360dialog/{key}</c> is half of the webhook credential (the secret is the other half), so it never goes into
/// the error registry nor into a log line: it is replaced by <c>{channelKey}</c>.
/// </summary>
public static partial class RequestPathRedactor
{
    public const string Placeholder = "{channelKey}";

    public static string? Redact(string? path)
        => string.IsNullOrEmpty(path) ? path : WebhookPath().Replace(path, "${prefix}" + Placeholder);

    /// <summary>True for the webhook route: a failure there may quote customer data, so its exception is described (masked), never printed raw.</summary>
    public static bool IsWebhookPath(string? path) => !string.IsNullOrEmpty(path) && WebhookPath().IsMatch(path);

    /// <summary>The route values of the request as a dictionary for the error report, with the channel key redacted.</summary>
    public static Dictionary<string, string?> RedactRouteValues(IEnumerable<KeyValuePair<string, object?>> routeValues)
        => routeValues.ToDictionary(v => v.Key, v => v.Key.Equals("channelKey", StringComparison.OrdinalIgnoreCase) ? Placeholder : v.Value?.ToString());

    [GeneratedRegex("(?<prefix>/" + WebhookRoutes.Dialog360Base + "/)[^/?#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebhookPath();
}
