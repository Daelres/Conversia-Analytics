using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;

namespace Conversia.Application.Reports;

/// <summary>What the model answered, read: the content and the names it says it used.</summary>
public sealed record LlmReportAnswer(ReportContent Content, IReadOnlyList<string> MentionedNames);

/// <summary>
/// The request the model reads for a report and the reading of its answer (docs/REPORTS.md, "The model"): the static prompt and field
/// guide as the cacheable prefix, the data pack (JSON) as the only variable part, and a JSON schema for the structured answer.
/// </summary>
public static class LlmReportWriter
{
    public static LlmRequest BuildRequest(ReportDataPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var json = pack.ToJson();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReportPrompt.Revision + "\n" + json)));
        return new LlmRequest(ReportPrompt.System, ReportPrompt.Context, "<data_pack>\n" + json + "\n</data_pack>", Schema(), hash);
    }

    /// <summary>The structured answer: every property required, no others (the limits are the validator's).</summary>
    public static JsonObject Schema()
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject Strings() => new() { ["type"] = "array", ["items"] = Text() };

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("title", "indicators", "diagnosis", "actions", "coverage_note", "mentioned_names"),
            ["properties"] = new JsonObject
            {
                ["title"] = Text(),
                ["indicators"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("label", "value", "note"),
                        ["properties"] = new JsonObject { ["label"] = Text(), ["value"] = Text(), ["note"] = Text() },
                    },
                },
                ["diagnosis"] = Text(),
                ["actions"] = Strings(),
                ["coverage_note"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["mentioned_names"] = Strings(),
            },
        };
    }

    /// <summary>The answer as content, or null when it is not JSON of the schema's shape (the composer counts it as an invalid answer).</summary>
    public static LlmReportAnswer? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || root["title"]?.GetValue<string>() is not { } title
                || root["diagnosis"]?.GetValue<string>() is not { } diagnosis
                || root["indicators"] is not JsonArray indicators
                || root["actions"] is not JsonArray actions)
            {
                return null;
            }

            var items = indicators.Select(i => i is JsonObject o
                    ? new ReportIndicator(o["label"]?.GetValue<string>() ?? string.Empty, o["value"]?.GetValue<string>() ?? string.Empty, o["note"]?.GetValue<string>() ?? string.Empty)
                    : null)
                .ToList();
            if (items.Any(i => i is null))
            {
                return null;
            }

            var names = root["mentioned_names"] is JsonArray mentioned ? mentioned.Select(n => n?.GetValue<string>() ?? string.Empty).ToList() : [];
            var coverage = root["coverage_note"] is JsonValue note && note.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
            return new LlmReportAnswer(
                new ReportContent(title.Trim(), items!, diagnosis.Trim(), actions.Select(a => a?.GetValue<string>()?.Trim() ?? string.Empty).ToList(), coverage),
                names);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
