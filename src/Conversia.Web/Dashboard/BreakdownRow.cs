using Conversia.Application.DTOs.Metrics;
using Conversia.Domain.Common;
using Conversia.Web.Services;

namespace Conversia.Web.Dashboard;

/// <summary>One line of a breakdown: conversion and first response of a branch, an advisor or a source, side by side.</summary>
public sealed record BreakdownRow(
    string Key,
    string Name,
    int Total,
    int Won,
    int Decided,
    double? ConversionRate,
    double? MedianMinutes,
    double? WithinSlaPercent,
    int UnansweredOpen,
    bool InMaturation,
    bool LowSample)
{
    /// <summary>
    /// Decided conversations under which a rate is flagged "muestra baja". It mirrors the default of the API's <c>Metrics:MinSampleSize</c>,
    /// the sample the rate alerts themselves require; here it only warns, it hides nothing.
    /// </summary>
    public const int LowSampleDecided = 20;

    public const string UnassignedName = "Sin asignar";

    public static IReadOnlyList<BreakdownRow> ByBranch(DashboardData data)
        => Join(data.Conversion.ByBranch, data.FirstResponse.ByBranch, id => id?.ToString() ?? string.Empty, (id, name) => name);

    /// <summary>Conversations with no advisor form an explicit line.</summary>
    public static IReadOnlyList<BreakdownRow> ByAdvisor(DashboardData data)
        => Join(data.Conversion.ByAdvisor, data.FirstResponse.ByAdvisor, id => id?.ToString() ?? string.Empty, (id, name) => id is null ? UnassignedName : name);

    public static IReadOnlyList<BreakdownRow> BySource(DashboardData data)
        => Join(data.Conversion.BySource, data.FirstResponse.BySource, _ => string.Empty, (_, name) => ConversationLabels.Source(name), bySourceName: true);

    // The two endpoints group the same cohort the same way, so the lines match one to one; a line only one of them has still shows
    // (with the other half empty) instead of vanishing. Sources have no id: they are matched by their name.
    private static List<BreakdownRow> Join(
        IReadOnlyList<ConversionRow> conversion,
        IReadOnlyList<FirstResponseRow> response,
        Func<Guid?, string> keyOfId,
        Func<Guid?, string, string> displayName,
        bool bySourceName = false)
    {
        string KeyOf(Guid? id, string name) => bySourceName ? name : keyOfId(id);

        var responses = response.ToDictionary(row => KeyOf(row.Id, row.Name));
        var rows = new List<BreakdownRow>();
        var seen = new HashSet<string>();

        foreach (var row in conversion)
        {
            var key = KeyOf(row.Id, row.Name);
            seen.Add(key);
            responses.TryGetValue(key, out var first);
            rows.Add(new BreakdownRow(
                key, displayName(row.Id, row.Name), row.Total, row.Won, row.Decided, row.ConversionRate,
                first?.MedianMinutes, first?.AnsweredWithinSlaPercent, first?.UnansweredOpen ?? 0, row.InMaturation, !row.InMaturation && row.Decided < LowSampleDecided));
        }

        foreach (var (key, first) in responses.Where(entry => !seen.Contains(entry.Key)))
        {
            rows.Add(new BreakdownRow(
                key, displayName(first.Id, first.Name), first.Conversations, 0, 0, null,
                first.MedianMinutes, first.AnsweredWithinSlaPercent, first.UnansweredOpen, InMaturation: false, LowSample: true));
        }

        return rows;
    }

    /// <summary>The column a table can be ordered by; a null value always goes last, in either direction.</summary>
    public static IReadOnlyList<BreakdownRow> Sorted(IReadOnlyList<BreakdownRow> rows, string? sort)
    {
        var descending = sort is { Length: > 0 } && sort[0] == '-';
        var column = descending ? sort![1..] : sort;
        Func<BreakdownRow, IComparable?>? key = column switch
        {
            "name" => row => row.Name,
            "total" => row => row.Total,
            "conversion" => row => row.ConversionRate,
            "response" => row => row.MedianMinutes,
            "sla" => row => row.WithinSlaPercent,
            "unanswered" => row => row.UnansweredOpen,
            _ => null,
        };

        if (key is null)
        {
            return rows;
        }

        var withValue = rows.Where(row => key(row) is not null);
        var ordered = descending
            ? withValue.OrderByDescending(key, ValueComparer.Instance)
            : withValue.OrderBy(key, ValueComparer.Instance);
        return ordered.Concat(rows.Where(row => key(row) is null)).ToList();
    }

    private sealed class ValueComparer : IComparer<IComparable?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(IComparable? x, IComparable? y)
            => x is string left && y is string right
                ? string.Compare(left, right, SpanishCulture.Info, System.Globalization.CompareOptions.IgnoreCase)
                : x?.CompareTo(y) ?? (y is null ? 0 : -1);
    }

    /// <summary>What one more click on a column header asks for: the first click puts the most useful end first, the next one flips it.</summary>
    public static string NextSort(string? current, string column)
    {
        var descending = current is { Length: > 0 } && current[0] == '-';
        var active = descending ? current![1..] : current;
        if (active == column)
        {
            return descending ? column : "-" + column;
        }

        // Counts and rates read best biggest first; names and waiting times, smallest first.
        return column is "name" or "response" ? column : "-" + column;
    }

    /// <summary>The order a query string asks for, or null when it is not one of the columns.</summary>
    public static string? ValidSort(string? sort)
    {
        var column = sort is { Length: > 0 } && sort[0] == '-' ? sort[1..] : sort;
        return column is not null && SortColumns.Contains(column) ? sort : null;
    }

    public static IReadOnlyList<string> SortColumns { get; } = ["name", "total", "conversion", "response", "sla", "unanswered"];
}
