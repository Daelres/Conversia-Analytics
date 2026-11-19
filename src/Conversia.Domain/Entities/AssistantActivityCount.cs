namespace Conversia.Domain.Entities;

/// <summary>
/// One counter of the AI sales assistant's activity per company, local day (America/Bogota), sandbox flag and metric (docs/ASSISTANT.md,
/// "Monitoring"): timers armed, fired and discarded, handoffs by reason, messages sent. Counts only, never a conversation or a text. Upserted in
/// SQL; kept 90 days.
/// </summary>
public class AssistantActivityCount
{
    public const int MetricMaxLength = 64;

    public Guid TenantId { get; private set; }

    public DateOnly Day { get; private set; }

    public bool IsSandbox { get; private set; }

    public string Metric { get; private set; } = string.Empty;

    public int Count { get; private set; }
}

/// <summary>The metrics of <see cref="AssistantActivityCount"/>. A handoff adds its reason: <c>handoff.max_turns</c>.</summary>
public static class AssistantMetrics
{
    public const string TimerArmed = "timer_armed";
    public const string TimerFired = "timer_fired";
    public const string Discarded = "discarded";
    public const string Superseded = "superseded";
    public const string Replied = "replied";
    public const string HandoffPrefix = "handoff.";
    public const string HandoffLineSent = "handoff_line_sent";
    public const string SendFailed = "send_failed";
    public const string TakenOver = "taken_over";
    public const string HandedBack = "handed_back";

    public static string Handoff(string reason) => HandoffPrefix + reason;

    /// <summary>A discard with its reason: <c>discarded.human_answered</c>.</summary>
    public static string DiscardedBecause(string reason) => Discarded + "." + reason;
}
