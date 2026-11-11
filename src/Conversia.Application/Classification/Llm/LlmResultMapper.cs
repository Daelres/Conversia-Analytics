using System.Text.Json;
using System.Text.RegularExpressions;
using Conversia.Domain.Enums;

namespace Conversia.Application.Classification.Llm;

/// <param name="Result">The mapped reading; null when the answer was rejected.</param>
/// <param name="Error">Why it was rejected, as a short code (<c>missing:stage</c>, <c>invalid:confidence</c>); never the model's text.</param>
public sealed record LlmMapping(ClassificationResult? Result, string? Error)
{
    public bool Succeeded => Result is not null;
}

/// <summary>
/// Maps the model's JSON answer to a <see cref="ClassificationResult"/>, strictly (spec CC-3): an unknown property, an out-of-vocabulary
/// value, a malformed code or a confidence outside 0..1 rejects the whole answer (a content failure: the rules decide), so nothing is ever
/// partially applied. The only coercion is a well-formed service code outside the catalog, which becomes <see cref="LlmCatalog.Other"/>.
/// <c>stage</c>, <c>objection</c> and <c>confidence</c> are required (and <c>age_range</c> when it is asked, as in the schema); a missing
/// <c>gender</c> is undetermined, the other fields empty. A property given twice rejects the answer, and a cited evidence line must be shown in the prompt.
/// Rule codes are <c>llm.*</c>. The result holds codes and enums only.
/// </summary>
public sealed partial class LlmResultMapper
{
    private readonly LlmCatalog _catalog;
    private readonly bool _extractAgeRange;
    private readonly HashSet<string> _allowed;

    public LlmResultMapper(LlmCatalog catalog, bool extractAgeRange)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _extractAgeRange = extractAgeRange;
        _allowed = ["stage", "loss_reason", "objection", "services", "interest_reason", "gender", "confidence", "won_evidence_line"];
        if (extractAgeRange)
        {
            _allowed.Add("age_range");
        }
    }

    /// <param name="json">The model's answer.</param>
    /// <param name="presentLines">The message numbers shown in the prompt (<see cref="PreprocessedConversation.PresentNumbers"/>): a cited
    /// evidence line must be one of them, not a dropped message nor one the budget left out.</param>
    public LlmMapping Map(string? json, IReadOnlySet<int> presentLines)
    {
        ArgumentNullException.ThrowIfNull(presentLines);
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            return Map(document.RootElement, presentLines);
        }
        catch (JsonException)
        {
            return Fail("invalid_json");
        }
    }

    private LlmMapping Map(JsonElement root, IReadOnlySet<int> presentLines)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail("invalid_json");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!_allowed.Contains(property.Name))
            {
                return Fail("unknown_property");
            }

            if (!seen.Add(property.Name))
            {
                return Fail("duplicate_property");
            }
        }

        if (_extractAgeRange && !root.TryGetProperty("age_range", out _))
        {
            return Fail("missing:age_range");
        }

        if (!TryEnum(root, "stage", ExtractionVocabulary.Stages, required: true, out var stage, out var error)
            || !TryEnum(root, "objection", ExtractionVocabulary.Objections, required: true, out var objection, out error)
            || !TryNullableEnum(root, "loss_reason", ExtractionVocabulary.LossReasons, out var lossReason, out error)
            || !TryEnum(root, "gender", ExtractionVocabulary.Genders, required: false, out var gender, out error))
        {
            return Fail(error!);
        }

        AgeRange? ageRange = null;
        if (_extractAgeRange && !TryNullableEnum(root, "age_range", ExtractionVocabulary.AgeRanges, out ageRange, out error))
        {
            return Fail(error!);
        }

        if (lossReason is not null && stage != ClassificationOutcome.Lost)
        {
            return Fail("invalid:loss_reason");
        }

        if (!root.TryGetProperty("confidence", out var confidenceElement))
        {
            return Fail("missing:confidence");
        }

        if (confidenceElement.ValueKind != JsonValueKind.Number || !confidenceElement.TryGetDecimal(out var confidence) || confidence is < 0m or > 1m)
        {
            return Fail("invalid:confidence");
        }

        var codes = new List<string> { "llm.outcome." + Code(ExtractionVocabulary.Stages, stage) };
        if (lossReason is { } loss)
        {
            codes.Add("llm.loss." + Code(ExtractionVocabulary.LossReasons, loss));
        }

        if (objection != ObjectionType.None)
        {
            codes.Add("llm.objection." + Code(ExtractionVocabulary.Objections, objection));
        }

        if (!TryServices(root, out var services, out var coerced))
        {
            return Fail("invalid:services");
        }

        if (coerced)
        {
            codes.Add("llm.service." + LlmCatalog.Other);
        }

        string? reason = null;
        if (root.TryGetProperty("interest_reason", out var reasonElement) && reasonElement.ValueKind != JsonValueKind.Null)
        {
            reason = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
            if (reason is null || !_catalog.InterestReasons.Contains(reason, StringComparer.Ordinal))
            {
                return Fail("invalid:interest_reason");
            }
        }

        int? evidence = null;
        if (root.TryGetProperty("won_evidence_line", out var evidenceElement) && evidenceElement.ValueKind != JsonValueKind.Null)
        {
            if (evidenceElement.ValueKind != JsonValueKind.Number || !evidenceElement.TryGetInt32(out var line) || !presentLines.Contains(line))
            {
                return Fail("invalid:won_evidence_line");
            }

            evidence = line;
        }

        var result = new ClassificationResult(
            services.Count == 0 ? null : string.Join(',', services),
            reason,
            objection,
            stage,
            lossReason,
            Math.Round(confidence, 2),
            codes,
            ageRange,
            gender,
            evidence);
        return new LlmMapping(result, null);
    }

    private bool TryServices(JsonElement root, out List<string> services, out bool coerced)
    {
        services = [];
        coerced = false;
        if (!root.TryGetProperty("services", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > ExtractionVocabulary.MaxServices)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            var code = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (code is null || !CodePattern().IsMatch(code))
            {
                return false;
            }

            if (!_catalog.Services.Contains(code, StringComparer.Ordinal))
            {
                code = LlmCatalog.Other;
                coerced = true;
            }

            if (!services.Contains(code))
            {
                services.Add(code);
            }
        }

        return true;
    }

    private static bool TryEnum<T>(JsonElement root, string name, IReadOnlyList<(string Code, T Value)> vocabulary, bool required, out T value, out string? error)
        where T : struct, Enum
    {
        value = default;
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            error = required ? "missing:" + name : null;
            return !required;
        }

        if (element.ValueKind == JsonValueKind.String && Lookup(vocabulary, element.GetString(), out value))
        {
            return true;
        }

        error = "invalid:" + name;
        return false;
    }

    private static bool TryNullableEnum<T>(JsonElement root, string name, IReadOnlyList<(string Code, T Value)> vocabulary, out T? value, out string? error)
        where T : struct, Enum
    {
        value = null;
        error = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && Lookup(vocabulary, element.GetString(), out var found))
        {
            value = found;
            return true;
        }

        error = "invalid:" + name;
        return false;
    }

    private static bool Lookup<T>(IReadOnlyList<(string Code, T Value)> vocabulary, string? code, out T value)
        where T : struct, Enum
    {
        foreach (var (c, v) in vocabulary)
        {
            if (string.Equals(c, code, StringComparison.Ordinal))
            {
                value = v;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Code<T>(IReadOnlyList<(string Code, T Value)> vocabulary, T value)
        where T : struct, Enum
        => vocabulary.First(v => EqualityComparer<T>.Default.Equals(v.Value, value)).Code;

    private static LlmMapping Fail(string error) => new(null, error);

    // The shape of a code of the rules file: what Opportunity accepts, one code at a time.
    [GeneratedRegex("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
