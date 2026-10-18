using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.TestSupport;
using Xunit.Sdk;

namespace Conversia.IntegrationTests;

/// <summary>The Spanish texts the requirement fixes for the API (phase "i18n-es"); every other message is the implementer's choice.</summary>
internal static class ApiMessages
{
    public const string InvalidCredentials = "Correo o contraseña incorrectos.";
    public const string DuplicateEmail = "Ese correo ya está registrado.";
    public const string ProductNotFound = "Producto no encontrado.";
    public const string UserNotFound = "Usuario no encontrado.";
    public const string DuplicateSku = "Ya existe un producto con ese SKU.";
    public const string Concurrency = "El producto fue modificado por otra persona. Recarga e inténtalo de nuevo.";
    public const string RateLimited = "Demasiados intentos de inicio de sesión. Inténtalo de nuevo más tarde.";
    public const string Unexpected = "Ocurrió un error inesperado. Inténtalo de nuevo más tarde.";
    public const string UnexpectedTitle = "Ocurrió un error inesperado";
    public const string ValidationTitle = "Uno o más errores de validación.";
}

/// <summary>
/// Reads every user-facing text of a problem+json answer (<c>title</c>, <c>detail</c> and every validation message under
/// <c>errors</c>) and collects, across many requests, the ones that are English or still a framework default.
/// A failing test therefore lists every untranslated message at once. Property names (the keys under <c>errors</c>) are
/// identifiers and are not scanned; developer-facing startup exceptions never reach this class.
/// </summary>
internal sealed class ApiMessageReport
{
    private readonly List<string> _problems = new();
    private readonly List<string> _checked = new();

    public int CasesChecked => _checked.Count;

    /// <summary>The texts of one problem body, tagged by where they were found.</summary>
    public static IReadOnlyList<(string Where, string Text)> TextsOf(string body)
    {
        var texts = new List<(string, string)>();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return texts;
        }

        foreach (var name in new[] { "title", "detail" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                texts.Add((name, value.GetString()!));
            }
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in errors.EnumerateObject())
            {
                foreach (var message in field.Value.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String))
                {
                    texts.Add(($"errors.{field.Name}", message.GetString()!));
                }
            }
        }

        return texts;
    }

    /// <summary>Checks one answer: the expected status, a problem+json body with text in it, no English and no framework default in any text.</summary>
    public async Task CheckAsync(string label, HttpResponseMessage response, HttpStatusCode expected, bool validationProblem = false, bool requireExplanation = true)
    {
        _checked.Add(label);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != expected)
        {
            _problems.Add($"[{label}] expected {(int)expected} but got {(int)response.StatusCode}: {Snippet(body)}");
            return;
        }

        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            _problems.Add($"[{label}] content type is '{response.Content.Headers.ContentType?.MediaType}', expected application/problem+json: {Snippet(body)}");
            return;
        }

        IReadOnlyList<(string Where, string Text)> texts;
        try
        {
            texts = TextsOf(body);
        }
        catch (JsonException)
        {
            _problems.Add($"[{label}] the problem body is not JSON: {Snippet(body)}");
            return;
        }

        if (texts.All(t => t.Where == "title") && validationProblem)
        {
            _problems.Add($"[{label}] a validation problem without any validation message: {Snippet(body)}");
        }

        if (requireExplanation && !texts.Any(t => t.Where is "detail" || t.Where.StartsWith("errors.", StringComparison.Ordinal)))
        {
            _problems.Add($"[{label}] the answer explains nothing (no detail, no validation message): {Snippet(body)}");
        }

        if (validationProblem && texts.FirstOrDefault(t => t.Where == "title").Text != ApiMessages.ValidationTitle)
        {
            _problems.Add($"[{label}] ValidationProblemDetails.title is '{texts.FirstOrDefault(t => t.Where == "title").Text}', expected '{ApiMessages.ValidationTitle}'.");
        }

        var all = string.Join(" ", texts.Select(t => t.Text));
        if (!EnglishLeaks.LooksSpanish(all))
        {
            _problems.Add($"[{label}] the texts contain no Spanish word: {Snippet(all)}");
        }

        foreach (var (where, text) in texts)
        {
            foreach (var leak in EnglishLeaks.Find(text))
            {
                _problems.Add($"[{label}] English wording \"{leak}\" in {where}: {Snippet(text)}");
            }
        }
    }

    public void AssertClean()
    {
        if (_checked.Count == 0)
        {
            throw new XunitException("No answer was checked.");
        }

        if (_problems.Count == 0)
        {
            return;
        }

        var message = new StringBuilder().AppendLine($"{_problems.Count} problem(s) in {_checked.Count} answer(s) checked:");
        foreach (var problem in _problems)
        {
            message.AppendLine("  - " + problem);
        }

        throw new XunitException(message.ToString());
    }

    private static string Snippet(string text) => text.Length <= 160 ? text : text[..160] + "...";
}
