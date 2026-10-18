namespace Conversia.Web.Dashboard;

/// <summary>
/// The six headline numbers of the dashboard (docs/METRICS.md defines each one; this only picks them from the API's answers).
/// Percentages are 0 to 100, as the API sends them. A number that cannot be computed is null, never zero.
/// </summary>
public sealed record DashboardKpis(
    int Conversations,
    double? ConversionRate,
    bool InMaturation,
    int Matured,
    int Immature,
    int Won,
    int Lost,
    int Following,
    double? MedianFirstResponseMinutes,
    double? WithinSlaPercent,
    int SlaMinutes,
    int UnansweredOpen,
    int UnansweredOpenOverSla,
    int UnansweredClosed,
    double? OldestUnansweredOpenMinutes,
    string? TopLossReason,
    int TopLossCount,
    double? TopLossSharePercent,
    int StatedLost,
    int NoResponseCount,
    int TotalLost,
    double? PreviousConversionRate,
    double? DeltaPoints)
{
    /// <summary>Below this many points the change is not worth announcing.</summary>
    public const double FlatThreshold = 0.05;

    public static DashboardKpis From(DashboardData data)
    {
        var conversion = data.Summary.Conversion;
        var response = data.Summary.FirstResponse;
        var top = data.Summary.TopLossReasons.FirstOrDefault();
        var previous = data.Previous?.ConversionRate;
        var delta = conversion.ConversionRate is { } now && previous is { } before ? now - before : (double?)null;

        return new DashboardKpis(
            Conversations: conversion.Total,
            ConversionRate: conversion.ConversionRate,
            InMaturation: conversion.InMaturation,
            Matured: conversion.Matured,
            Immature: conversion.Immature,
            Won: conversion.Won,
            Lost: conversion.Lost,
            Following: conversion.InFollowUp + conversion.New,
            MedianFirstResponseMinutes: response.MedianMinutes,
            WithinSlaPercent: response.AnsweredWithinSlaPercent,
            SlaMinutes: response.SlaMinutes,
            UnansweredOpen: response.UnansweredOpen,
            UnansweredOpenOverSla: response.UnansweredOpenOverSla,
            UnansweredClosed: response.UnansweredClosed,
            OldestUnansweredOpenMinutes: response.OldestUnansweredOpenMinutes,
            TopLossReason: top?.Reason,
            TopLossCount: top?.Count ?? 0,
            TopLossSharePercent: top?.SharePercent,
            StatedLost: data.Summary.StatedLost,
            NoResponseCount: data.Summary.NoResponse.Count,
            TotalLost: data.Summary.TotalLost,
            PreviousConversionRate: previous,
            DeltaPoints: delta);
    }

    public TrendDirection Trend => DeltaPoints switch
    {
        null => TrendDirection.Unknown,
        { } delta when Math.Abs(delta) < FlatThreshold => TrendDirection.Flat,
        > 0 => TrendDirection.Up,
        _ => TrendDirection.Down,
    };
}

public enum TrendDirection
{
    Unknown,
    Up,
    Down,
    Flat,
}
