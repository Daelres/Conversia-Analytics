using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conversia.Application.Reports;

/// <summary>
/// Everything a report may say, computed from the metrics of the period and scope (docs/REPORTS.md, "The data pack"). <b>Aggregates only</b>:
/// counts, rates, minutes, and the names of the company, its branches and advisors (company-internal). Never a customer's name, number or
/// message: the types below have no field that could hold one, and a guard test walks them. The model and the template write from this and
/// nothing else, and the grounding validator checks every number and name of their text against it.
/// </summary>
/// <param name="Version">The shape of the pack; bump it with any change of the fields.</param>
/// <param name="Frequency">"diario", "semanal" or "mensual".</param>
/// <param name="Scope">"Todas las sucursales" or the names of the chosen ones.</param>
/// <param name="Sections">The sections the definition asked for, by name.</param>
public sealed record ReportDataPack(
    string Version,
    string Company,
    string Frequency,
    ReportPackPeriod Period,
    string Scope,
    IReadOnlyList<string> Sections,
    ReportHeadline? Headline,
    ReportConversion? Conversion,
    ReportDropOff? DropOff,
    ReportLosses? Losses,
    ReportResponseTimes? Response,
    ReportCoverage? Coverage)
{
    public const string CurrentVersion = "1";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Letters of every language stay readable, but <, >, &, ' and " are escaped: a name can never close the prompt's <data_pack> block.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReportDataPack FromJson(string json) => JsonSerializer.Deserialize<ReportDataPack>(json, JsonOptions)
        ?? throw new JsonException("The data pack is empty.");

    /// <summary>Every name the pack holds (company, branches, advisors, sources and labels): what a text may name.</summary>
    public IEnumerable<string> Names()
    {
        yield return Company;
        yield return Scope;
        yield return Period.Label;
        if (Headline is { } headline)
        {
            yield return headline.Conversion.Label;
            yield return headline.PreviousConversion.Label;
        }

        if (Conversion is { } conversion)
        {
            yield return conversion.WindowLabel;
        }

        foreach (var name in new[] { Conversion?.ByBranch, Conversion?.ByAdvisor, Conversion?.BySource }.Where(l => l is not null).SelectMany(l => l!).Select(r => r.Name))
        {
            yield return name;
        }

        foreach (var name in Response?.SlowestBranches.Select(b => b.Name) ?? [])
        {
            yield return name;
        }

        foreach (var label in (Losses?.TopReasons ?? []).Concat(Losses?.TopObjections ?? []).Select(s => s.Label))
        {
            yield return label;
        }

        if (DropOff?.MainStage is { } stage)
        {
            yield return stage;
        }
    }
}

/// <param name="From">First day, inclusive.</param>
/// <param name="Until">Last day, <b>inclusive</b> (how a person reads a period).</param>
/// <param name="Label">The period in words: "semana del 9 al 15 de noviembre de 2026".</param>
public sealed record ReportPackPeriod(DateOnly From, DateOnly Until, int Days, string Label);

/// <summary>
/// A conversion rate as the report states it (docs/REPORTS.md, "Conversion"): measured over a window whose conversations are all old enough to
/// be decided (<c>Metrics:ConversionMaturityDays</c>), so a young period never shows a rate from a handful of chats. <see cref="Rate"/> is null
/// when the window has fewer than the minimum decided conversations (<c>Reports:MinConversionSample</c>).
/// </summary>
/// <param name="From">First day of the window, inclusive.</param>
/// <param name="Until">Last day of the window, inclusive.</param>
/// <param name="Label">The window in words ("del 2 al 8 de noviembre").</param>
/// <param name="Decided">Decided conversations of the window: the denominator.</param>
public sealed record ReportConversionRate(DateOnly From, DateOnly Until, string Label, int Decided, int Won, double? Rate);

/// <summary>The headline: the period's conversations and sales, and the conversion of its latest matured window against the window before.</summary>
/// <param name="Conversations">Conversations with customers that started in the report's period.</param>
/// <param name="Won">Sales closed so far among them.</param>
/// <param name="PreviousConversations">The same, for the period of the same kind right before.</param>
/// <param name="ConversionChangePoints">Current minus previous rate, one decimal; null unless both rates passed the minimum sample.</param>
public sealed record ReportHeadline(
    int Conversations,
    int Won,
    int PreviousConversations,
    int MaturityDays,
    int MinSample,
    ReportConversionRate Conversion,
    ReportConversionRate PreviousConversion,
    double? ConversionChangePoints);

/// <param name="Name">Branch, advisor or source name.</param>
public sealed record ReportGroupRate(string Name, int Conversations, int Decided, int Won, double? ConversionRate);

/// <param name="WindowLabel">The matured window the rates are measured over (the headline's).</param>
/// <param name="MinSample">Decided conversations a group needs to have a rate (<c>Reports:MinConversionSample</c>); below it the rate is null.</param>
public sealed record ReportConversion(
    DateOnly WindowFrom,
    DateOnly WindowUntil,
    string WindowLabel,
    double? CompanyRate,
    int MinSample,
    IReadOnlyList<ReportGroupRate> ByBranch,
    IReadOnlyList<ReportGroupRate> ByAdvisor,
    IReadOnlyList<ReportGroupRate> BySource);

/// <summary>Where the conversations without a sale stopped (docs/METRICS.md, "Closing point").</summary>
/// <param name="MainStage">The step where most of them stopped, in words; null when none was read.</param>
public sealed record ReportDropOff(int Stopped, int Read, string? MainStage, int? MainStageCount, double? MainStageSharePercent, int MovedOffChannel);

/// <param name="SharePercent">0 to 100, one decimal.</param>
public sealed record ReportShare(string Label, int Count, double? SharePercent);

/// <param name="NoResponse">Sales lost because the customer stopped answering (inferred from silence; not one of the stated reasons).</param>
public sealed record ReportLosses(int TotalLost, int NoResponse, double? NoResponseSharePercent, IReadOnlyList<ReportShare> TopReasons, IReadOnlyList<ReportShare> TopObjections);

/// <summary>First response of the period's conversations, and the open conversations waiting for a reply <b>now</b>, whenever they started.</summary>
/// <param name="OpenUnansweredNow">Open conversations nobody has answered, at the moment the report is written (any start date).</param>
/// <param name="OpenOverSlaNow">Of those, the ones waiting longer than the SLA.</param>
/// <param name="OldestWaitingHours">How long the oldest of them has waited.</param>
public sealed record ReportResponseTimes(
    int SlaMinutes,
    double? MedianMinutes,
    double? WithinSlaPercent,
    int Answered,
    int OpenUnansweredNow,
    int OpenOverSlaNow,
    double? OldestWaitingHours,
    IReadOnlyList<ReportGroupResponse> SlowestBranches);

public sealed record ReportGroupResponse(string Name, double? MedianMinutes, double? WithinSlaPercent, int Answered);

/// <summary>How much of the period the AI read: the stopped conversations whose step was read, and whether the AI is on.</summary>
public sealed record ReportCoverage(bool AiEnabled, int Stopped, int NotRead, double? ReadSharePercent, DateOnly? LastUpdatedOn);
