using System.ComponentModel.DataAnnotations;
using Conversia.Application.Metrics;
using Conversia.Application.Validation;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Metrics;

/// <summary>
/// Query string of every <c>/api/metrics</c> endpoint (docs/METRICS.md). <c>From</c>/<c>To</c> are local dates of the tenant's time zone,
/// <c>From</c> inclusive and <c>To</c> exclusive; with neither, the last 30 days including today.
/// </summary>
public class MetricsQuery : IValidatableObject
{
    [Display(Name = "Desde")]
    public DateOnly? From { get; set; }

    [Display(Name = "Hasta")]
    public DateOnly? To { get; set; }

    [Display(Name = "Sucursal")]
    public Guid? BranchId { get; set; }

    [Display(Name = "Asesor")]
    public Guid? AdvisorId { get; set; }

    /// <summary>Only conversations with no advisor. Cannot be combined with <see cref="AdvisorId"/>.</summary>
    [Display(Name = "Sin asesor")]
    public bool? Unassigned { get; set; }

    [Display(Name = "Origen")]
    [EnumName(typeof(ConversationSource))]
    public string? Source { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to)
        {
            if (from >= to)
            {
                yield return new ValidationResult("El campo Desde debe ser anterior al campo Hasta.", [nameof(From), nameof(To)]);
            }
            else if (to.DayNumber - from.DayNumber > MetricsOptions.MaxRangeDays)
            {
                yield return new ValidationResult($"El rango de fechas no puede superar los {MetricsOptions.MaxRangeDays} días.", [nameof(From), nameof(To)]);
            }
        }

        if (AdvisorId is not null && Unassigned == true)
        {
            yield return new ValidationResult("No se puede filtrar por un asesor y por conversaciones sin asesor a la vez.", [nameof(AdvisorId), nameof(Unassigned)]);
        }
    }
}

/// <param name="From">First local date, inclusive.</param>
/// <param name="To">Last local date, exclusive.</param>
/// <param name="TimeZone">IANA name of the zone the dates are in.</param>
/// <param name="Days">Days in the period.</param>
public sealed record MetricsPeriod(DateOnly From, DateOnly To, string TimeZone, int Days);

/// <summary>Conversations of a group by stage, and the conversion rate they give (docs/METRICS.md, "Conversion").</summary>
public class ConversionCounts
{
    public int Total { get; set; }

    public int Won { get; set; }

    public int Lost { get; set; }

    public int InFollowUp { get; set; }

    public int New { get; set; }

    /// <summary>Of <see cref="InFollowUp"/> and <see cref="New"/>, those whose last message is older than the maturity: undecided and gone cold.</summary>
    public int Stale { get; set; }

    /// <summary>Still maturing: in follow-up or new and not stale yet.</summary>
    public int InProgress { get; set; }

    /// <summary><c>Won + Lost + Stale</c>: the denominator of the conversion rate.</summary>
    public int Decided { get; set; }

    /// <summary>Percent, one decimal: <c>Won / Decided</c>; null when nothing is decided.</summary>
    public double? ConversionRate { get; set; }

    /// <summary>Percent, one decimal: <c>Won / Total</c>, ignoring maturity; null when there are no conversations.</summary>
    public double? WinShareOfAll { get; set; }
}

public sealed class ConversionRow : ConversionCounts
{
    /// <summary>The branch or advisor; null for the "no advisor" bucket and for sources.</summary>
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class FirstResponseStats
{
    public int Conversations { get; set; }

    public int Answered { get; set; }

    /// <summary>Never counted as zero minutes and never part of the averages.</summary>
    public int Unanswered { get; set; }

    public double? AvgMinutes { get; set; }

    public double? MedianMinutes { get; set; }

    public double? P90Minutes { get; set; }

    public int SlaMinutes { get; set; }

    public int WithinSla { get; set; }

    /// <summary>Answered, plus unanswered whose customer message is older than the SLA: the conversations that have had their chance.</summary>
    public int SlaEligible { get; set; }

    public double? AnsweredWithinSlaPercent { get; set; }

    public int UnansweredOverSla { get; set; }

    public double? OldestUnansweredMinutes { get; set; }
}

public sealed class FirstResponseRow : FirstResponseStats
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed record LossReasonShare(string Reason, int Count, double SharePercent);

public sealed record ObjectionShare(string Objection, int Count, double SharePercent);

public sealed record SummaryResponse(
    MetricsPeriod Period, ConversionCounts Conversion, FirstResponseStats FirstResponse, int TotalLost, IReadOnlyList<LossReasonShare> TopLossReasons);

public sealed record ConversionResponse(
    MetricsPeriod Period,
    ConversionCounts Totals,
    IReadOnlyList<ConversionRow> ByBranch,
    IReadOnlyList<ConversionRow> ByAdvisor,
    IReadOnlyList<ConversionRow> BySource);

public sealed record FirstResponseResponse(
    MetricsPeriod Period,
    FirstResponseStats Totals,
    IReadOnlyList<FirstResponseRow> ByBranch,
    IReadOnlyList<FirstResponseRow> ByAdvisor,
    IReadOnlyList<FirstResponseRow> BySource);

public sealed record LossReasonsResponse(
    MetricsPeriod Period,
    int Conversations,
    int TotalLost,
    IReadOnlyList<LossReasonShare> TopReasons,
    int OtherCount,
    IReadOnlyList<ObjectionShare> Objections);

public sealed record DayVolume(DateOnly Date, int Conversations, int InboundMessages, int OutboundMessages);

/// <param name="DayOfWeek">1 = Monday ... 7 = Sunday (ISO).</param>
/// <param name="Hour">0 to 23, local time.</param>
public sealed record HourVolume(int DayOfWeek, int Hour, int Conversations, int InboundMessages);

public sealed record VolumeResponse(
    MetricsPeriod Period,
    int Conversations,
    int InboundMessages,
    int OutboundMessages,
    IReadOnlyList<DayVolume> ByDay,
    IReadOnlyList<HourVolume> ByHourOfWeek);

public sealed record MetricAlert(string Code, string Severity, string Message, IReadOnlyDictionary<string, object?> Values);

public sealed record UnansweredConversation(
    Guid ConversationId, Guid BranchId, string? BranchName, Guid? AdvisorId, string? AdvisorName, DateTime FirstInboundAt, double WaitingMinutes);

public sealed record UnansweredSummary(int Total, IReadOnlyList<UnansweredConversation> Items);

public sealed record AlertsResponse(MetricsPeriod Period, DateTime GeneratedAt, IReadOnlyList<MetricAlert> Alerts, UnansweredSummary Unanswered);

public static class AlertCodes
{
    public const string UnansweredOverSla = "UNANSWERED_OVER_SLA";
    public const string ConversionDrop = "CONVERSION_DROP";
    public const string BranchBelowAverage = "BRANCH_BELOW_AVERAGE";
}

public static class AlertSeverities
{
    public const string Warning = "Warning";
    public const string Critical = "Critical";
}
