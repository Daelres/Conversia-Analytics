using System.Text.Json;

namespace Conversia.Web.Services;

/// <summary>Reads the per-field messages of an API 400 (ValidationProblemDetails, or a plain problem with a detail).</summary>
internal static class ProblemDetailsErrors
{
    public const string GeneralKey = "";

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
