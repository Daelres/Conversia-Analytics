using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conversia.Application.Reports;

/// <summary>One of the (at most three) indicators of a report: what it is, its value and one sentence about it.</summary>
public sealed record ReportIndicator(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("note")] string Note);

/// <summary>
/// The structured text of a report (docs/REPORTS.md, "Content"): a title, up to three indicators, a short diagnosis and up to five concrete
/// actions, plus an optional note on the AI's coverage. The model answers in this shape (JSON schema) and the template builds it; the email,
/// the report page and the WhatsApp notice are rendered from it.
/// </summary>
public sealed record ReportContent(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("indicators")] IReadOnlyList<ReportIndicator> Indicators,
    [property: JsonPropertyName("diagnosis")] string Diagnosis,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("coverage_note")] string? CoverageNote = null)
{
    public const int MaxIndicators = 3;
    public const int MaxActions = 5;
    public const int TitleMaxLength = 120;
    public const int LabelMaxLength = 60;
    public const int ValueMaxLength = 40;
    public const int NoteMaxLength = 240;
    public const int DiagnosisMaxLength = 900;
    public const int ActionMaxLength = 240;
    public const int CoverageNoteMaxLength = 300;

    /// <summary>Everything together: about five minutes of reading at most, usually much less.</summary>
    public const int TotalMaxLength = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ReportContent FromJson(string json) => JsonSerializer.Deserialize<ReportContent>(json, Json) ?? throw new JsonException("The content is empty.");

    /// <summary>Every text of the content, in reading order.</summary>
    public IEnumerable<string> Texts()
    {
        yield return Title;
        foreach (var indicator in Indicators)
        {
            yield return indicator.Label;
            yield return indicator.Value;
            yield return indicator.Note;
        }

        yield return Diagnosis;
        foreach (var action in Actions)
        {
            yield return action;
        }

        if (CoverageNote is not null)
        {
            yield return CoverageNote;
        }
    }

    /// <summary>The one line of the WhatsApp notice and the history list: the first indicator, or the title.</summary>
    public string Headline() => Indicators.Count > 0 ? $"{Indicators[0].Label}: {Indicators[0].Value}" : Title;
}
