using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Conversia.API.Localization;

/// <summary>
/// Copies a model state with every message that is not ours replaced by a Spanish one. The JSON input formatter files the
/// text of the <see cref="System.Text.Json.JsonException"/> (English, with a path and byte positions) under the JSON path of
/// the value (<c>$</c>, <c>$.price</c>); the key is kept so the caller still knows where the problem is, the text is not.
/// </summary>
internal static class SpanishModelState
{
    public static ModelStateDictionary Localize(ModelStateDictionary original)
    {
        var localized = new ModelStateDictionary();
        foreach (var (key, entry) in original)
        {
            foreach (var error in entry.Errors)
            {
                localized.AddModelError(key, MessageOf(key, error));
            }
        }

        return localized;
    }

    private static string MessageOf(string key, ModelError error)
    {
        if (error.Exception is not null || IsJsonPath(key))
        {
            return key is "" or JsonRoot ? ProblemTexts.BodyNotJson : ProblemTexts.ValueWrongFormat;
        }

        return string.IsNullOrWhiteSpace(error.ErrorMessage) ? ProblemTexts.InputInvalid : error.ErrorMessage;
    }

    private const string JsonRoot = "$";

    private static bool IsJsonPath(string key) => key.StartsWith(JsonRoot, StringComparison.Ordinal);
}
