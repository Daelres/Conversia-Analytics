using System.Text.Json.Nodes;

namespace Conversia.Application.Classification.Llm;

/// <summary>
/// The JSON schema of the structured answer (<c>additionalProperties: false</c>, every property required, closed enums). The age range is
/// part of it only when <c>Classification:Llm:ExtractAgeRange</c> is true (off by default; spec CP-5). Bump <see cref="Version"/> whenever
/// the shape changes: it is an input of the snapshot hash.
/// </summary>
public sealed class ExtractionSchema
{
    public const string Version = "1";

    public ExtractionSchema(LlmCatalog catalog, bool extractAgeRange)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        ExtractAgeRange = extractAgeRange;
    }

    public LlmCatalog Catalog { get; }

    public bool ExtractAgeRange { get; }

    /// <summary>A fresh copy of the schema (callers may attach it to a request object).</summary>
    public JsonObject ToJson()
    {
        static JsonArray Codes(IEnumerable<string?> codes) => [.. codes.Select(c => (JsonNode?)c)];

        var properties = new JsonObject
        {
            ["stage"] = new JsonObject { ["type"] = "string", ["enum"] = Codes(ExtractionVocabulary.Stages.Select(s => s.Code)) },
            ["loss_reason"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = Codes(ExtractionVocabulary.LossReasons.Select(s => s.Code).Append(null)),
            },
            ["objection"] = new JsonObject { ["type"] = "string", ["enum"] = Codes(ExtractionVocabulary.Objections.Select(s => s.Code)) },
            ["services"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Codes(Catalog.Services) },
                ["maxItems"] = ExtractionVocabulary.MaxServices,
            },
            ["interest_reason"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = Codes(Catalog.InterestReasons.Append(null)),
            },
            ["gender"] = new JsonObject { ["type"] = "string", ["enum"] = Codes(ExtractionVocabulary.Genders.Select(s => s.Code)) },
        };

        if (ExtractAgeRange)
        {
            properties["age_range"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = Codes(ExtractionVocabulary.AgeRanges.Select(s => s.Code).Append(null)),
            };
        }

        properties["confidence"] = new JsonObject { ["type"] = "number" };
        properties["won_evidence_line"] = new JsonObject { ["type"] = new JsonArray("integer", "null") };

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = Codes(properties.Select(p => p.Key)),
            ["properties"] = properties,
        };
    }
}
