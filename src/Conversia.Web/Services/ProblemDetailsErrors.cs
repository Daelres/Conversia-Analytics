using System.Text.Json;

namespace Conversia.Web.Services;

/// <summary>Reads the per-field messages of an API 400 (ValidationProblemDetails, or a plain problem with a detail).</summary>
internal static class ProblemDetailsErrors
{
    public const string GeneralKey = "";

    /// <summary>The stable machine code of the API's 403 for a company that is pending or suspended.</summary>
    public const string TenantNotActiveCode = "tenant_not_active";

    private const int MaxDetailLength = 300;

    /// <summary>The <c>detail</c> of a problem document (the API's Spanish message), trimmed and capped; null when there is none.</summary>
    public static string? TryReadDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && ReadDetail(document.RootElement) is { } detail && !string.IsNullOrWhiteSpace(detail)
                ? detail.Trim().Length <= MaxDetailLength ? detail.Trim() : detail.Trim()[..MaxDetailLength]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The machine <c>code</c> of a problem document and the <c>tenantStatus</c> beside it; null when the body has no code.</summary>
    public static (string Code, string? TenantStatus)? TryReadCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String
                || code.GetString() is not { Length: > 0 } text)
            {
                return null;
            }

            var status = root.TryGetProperty("tenantStatus", out var tenantStatus) && tenantStatus.ValueKind == JsonValueKind.String
                ? tenantStatus.GetString()
                : null;
            return (text, status);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when the body is not a recognisable problem document.</summary>
    public static IReadOnlyDictionary<string, string[]>? TryParse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var errors = ReadErrors(document.RootElement);
            if (errors.Count == 0 && ReadDetail(document.RootElement) is { } detail)
            {
                errors[GeneralKey] = [detail];
            }

            return errors.Count == 0 ? null : errors;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string[]> ReadErrors(JsonElement root)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var field in errors.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Array))
        {
            var messages = field.Value.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString()!)
                .ToArray();
            if (messages.Length > 0)
            {
                result[field.Name] = messages;
            }
        }

        return result;
    }

    private static string? ReadDetail(JsonElement root)
        => root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
            ? detail.GetString()
            : null;
}
