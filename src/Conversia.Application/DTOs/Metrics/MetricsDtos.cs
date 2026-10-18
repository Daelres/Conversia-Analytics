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
        // Each date alone first: an extreme one must be refused before any arithmetic with it, also when the other end is missing.
        var fromProblem = DateWindow.Check(From, "Desde", nameof(From), validationContext);
        var toProblem = DateWindow.Check(To, "Hasta", nameof(To), validationContext);
        if (fromProblem is not null)
        {
            yield return fromProblem;
        }

        if (toProblem is not null)
        {
            yield return toProblem;
        }

        if (fromProblem is null && toProblem is null && From is { } from && To is { } to)
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

/// <summary>
/// Conversations of a group by stage, and the conversion rate they give (docs/METRICS.md, "Conversion"). The counts of the stages are the raw
/// numbers of the whole cohort; the rate is computed over its <b>matured</b> part only: the conversations that started at least
/// <c>Metrics:ConversionMaturityDays</c> ago, whose outcome is settled. All percentages of the metrics API go from 0 to 100.
/// </summary>
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

    /// <summary>Conversations that started at least the maturity ago: the cohort the rate is computed over.</summary>
    public int Matured { get; set; }

    /// <summary>The rest of <see cref="Total"/>: started less than the maturity ago. They are in every raw count and in none of the rate.</summary>
    public int Immature { get; set; }

    /// <summary><see cref="Won"/> among the <see cref="Matured"/> ones.</summary>
    public int MaturedWon { get; set; }

    /// <summary><see cref="Lost"/> among the <see cref="Matured"/> ones.</summary>
    public int MaturedLost { get; set; }

    /// <summary><c>MaturedWon + MaturedLost + Stale</c>: the denominator of the conversion rate.</summary>
    public int Decided { get; set; }

    /// <summary>
    /// The period has conversations but none is mature yet, so there is no rate to give (<see cref="ConversionRate"/> is null): not "0 %", not
    /// "unknown", just too early.
    /// </summary>
    public bool InMaturation { get; set; }

    /// <summary>Percent 0 to 100, one decimal: <c>MaturedWon / Decided</c>; null when <see cref="Decided"/> is 0 (see <see cref="InMaturation"/>).</summary>
    public double? ConversionRate { get; set; }

    /// <summary>Percent 0 to 100, one decimal: <c>Won / Total</c> over the whole cohort, ignoring maturity; null when there are no conversations.</summary>
    public double? WinShareOfAll { get; set; }
}

public sealed class ConversionRow : ConversionCounts
{
    /// <summary>The branch or advisor; null for the "no advisor" bucket and for sources.</summary>
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// First response of a group. "Unanswered" has one definition everywhere: the customer wrote and nobody replied. While the conversation is
/// <b>open</b> somebody still can (<see cref="UnansweredOpen"/>, the actionable set, also <c>GET /api/conversations?unanswered=true</c> and the alerts);
/// once it is <b>closed</b> nobody did (<see cref="UnansweredClosed"/>, a miss that is over).
/// </summary>
public class FirstResponseStats
{
    public int Conversations { get; set; }

    public int Answered { get; set; }

    /// <summary>Open, with no reply yet. Never counted as zero minutes and never part of the averages.</summary>
    public int UnansweredOpen { get; set; }

    /// <summary>Closed without any reply. Never part of the averages either.</summary>
    public int UnansweredClosed { get; set; }

    public double? AvgMinutes { get; set; }

    public double? MedianMinutes { get; set; }

    public double? P90Minutes { get; set; }

    public int SlaMinutes { get; set; }

    public int WithinSla { get; set; }

    /// <summary>Answered, plus open ones waiting longer than the SLA, plus every closed one: the conversations that have had their chance.</summary>
    public int SlaEligible { get; set; }

    /// <summary>Percent 0 to 100, one decimal; null when <see cref="SlaEligible"/> is 0.</summary>
    public double? AnsweredWithinSlaPercent { get; set; }

    /// <summary>Of <see cref="UnansweredOpen"/>, those whose customer message is older than the SLA.</summary>
    public int UnansweredOpenOverSla { get; set; }

    /// <summary>How long the oldest <b>open</b> unanswered conversation has been waiting; null when there is none. Closed ones never count.</summary>
    public double? OldestUnansweredOpenMinutes { get; set; }
}

public sealed class FirstResponseRow : FirstResponseStats
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <param name="SharePercent">Percent 0 to 100, one decimal, of the lost sales whose reason the customer stated (see <see cref="NoResponseLoss"/>); null over an empty set.</param>
public sealed record LossReasonShare(string Reason, int Count, double? SharePercent);

/// <summary>
/// Sales lost because the customer stopped answering. The system inferred it (the conversation closed for inactivity); the customer never said it,
/// so it is shown on its own line and is not part of the ranking of stated reasons.
/// </summary>
/// <param name="SharePercent">Percent 0 to 100, one decimal, of <c>totalLost</c>; null when nothing is lost.</param>
public sealed record NoResponseLoss(int Count, double? SharePercent);

/// <param name="SharePercent">Percent 0 to 100, one decimal, of the cohort; null when the cohort is empty.</param>
public sealed record ObjectionShare(string Objection, int Count, double? SharePercent);

/// <param name="TotalLost">Every lost sale of the cohort: <c>statedLost + noResponse.count</c>.</param>
/// <param name="StatedLost">Lost sales with a reason the customer stated or the classifier read (not inferred from silence).</param>
public sealed record SummaryResponse(
    MetricsPeriod Period,
    ConversionCounts Conversion,
    FirstResponseStats FirstResponse,
    int TotalLost,
    int StatedLost,
    NoResponseLoss NoResponse,
    IReadOnlyList<LossReasonShare> TopLossReasons);

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

/// <param name="TotalLost">Every lost sale of the cohort: <c>statedLost + noResponse.count</c>.</param>
/// <param name="StatedLost">Lost sales whose reason is not the silence of the customer; the shares of <paramref name="TopReasons"/> are of this number.</param>
/// <param name="NoResponse">The silent ones, on their own line.</param>
/// <param name="OtherCount">What the stated reasons outside the top add up to.</param>
public sealed record LossReasonsResponse(
    MetricsPeriod Period,
    int Conversations,
    int TotalLost,
    int StatedLost,
    NoResponseLoss NoResponse,
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

/// <param name="Unanswered">The <b>open</b> conversations waiting longer than the SLA (the same set as <c>GET /api/conversations?unanswered=true</c> older than the SLA).</param>
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
