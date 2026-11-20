using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The AI sales assistant's state in one conversation (docs/ASSISTANT.md, "State machine"), and its durable reply timer: a row in
/// <see cref="AssistantState.Waiting"/> with <see cref="DueAt"/> is a timer the assistant job claims. Every change is made under the
/// conversation's lock (the ingestion order of docs/WEBHOOKS.md); the job's claim only takes this row (<c>FOR UPDATE SKIP LOCKED</c>).
/// <see cref="Generation"/> changes whenever something happens that makes an answer being prepared stale (a customer or a person wrote, a
/// take-over, a hand-back, a handoff): the job remembers the generation it claimed and drops its answer when it no longer matches.
/// </summary>
public partial class ConversationAssistant : BaseEntity
{
    public const int HandoffReasonMaxLength = 40;

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>The conversation is a sandbox one (docs/SANDBOX.md); copied so the job and the counters never read it from elsewhere.</summary>
    public bool IsSandbox { get; private set; }

    public AssistantState State { get; private set; } = AssistantState.Off;

    /// <summary>When the armed timer fires; set only in <see cref="AssistantState.Waiting"/>.</summary>
    public DateTime? DueAt { get; private set; }

    /// <summary>Changes with every event that makes an answer in preparation stale.</summary>
    public int Generation { get; private set; }

    /// <summary>The job instance that claimed the timer holds it until then; a crashed instance's claim expires.</summary>
    public DateTime? ClaimedUntil { get; private set; }

    /// <summary>When the current timer was armed (the first unanswered customer message it waits on).</summary>
    public DateTime? ArmedAt { get; private set; }

    /// <summary>Assistant replies since the assistant (re)started in this conversation: the max-turns trigger counts them.</summary>
    public int AssistantTurns { get; private set; }

    /// <summary>Answers the reply generator could not give because it did not understand (set by the generator's result, unit 5b).</summary>
    public int MisunderstandingCount { get; private set; }

    /// <summary>Consecutive replies that brought an off-topic customer back to the company's topic (unit 5b): a second one in a row hands off.</summary>
    public int OffTopicCount { get; private set; }

    /// <summary>Why it was handed off (a code of <see cref="AssistantHandoffReasons"/>); null otherwise.</summary>
    public string? HandoffReason { get; private set; }

    public DateTime? HandedOffAt { get; private set; }

    public DateTime? HumanTookOverAt { get; private set; }

    /// <summary>
    /// In <see cref="AssistantState.HumanTookOver"/>: the assistant stays quiet until then (the cool-down after a person wrote). Null means
    /// until a person hands it back (an explicit take-over).
    /// </summary>
    public DateTime? HumanHoldUntil { get; private set; }

    /// <summary>The user who explicitly took the conversation over; null for a take-over by writing.</summary>
    public Guid? TakenOverBy { get; private set; }

    /// <summary>When the assistant first said it is an assistant in this conversation.</summary>
    public DateTime? DisclosedAt { get; private set; }

    public DateTime? LastRepliedAt { get; private set; }

    public const int TextHashLength = 64;

    /// <summary>Claims of the current timer: a timer that keeps failing is handed to a person (docs/ASSISTANT.md, "Pipeline").</summary>
    public int Attempts { get; private set; }

    /// <summary>The assistant committed to send (before the provider call): set until the message is recorded or the timer ends.</summary>
    public DateTime? SendingStartedAt { get; private set; }

    /// <summary>SHA-256 (hex) of the text being sent: an echo of the phone line with this text is the assistant's own message.</summary>
    public string? SendingTextHash { get; private set; }

    public bool IsSendPending => SendingStartedAt is not null;

    /// <summary>Marks the send about to be made; committed before the provider is called.</summary>
    public void BeginSend(DateTime now, string textHash)
    {
        if (State != AssistantState.Waiting)
        {
            throw new DomainException("El asistente solo envía en una conversación que espera su respuesta.");
        }

        SendingStartedAt = UtcTime.Normalize(now);
        SendingTextHash = textHash is { Length: > 0 and <= TextHashLength } ? textHash : throw new DomainException("El resumen del texto no es válido.");
    }

    /// <summary>The echo with <paramref name="textHash"/> is the pending send of the assistant (within <paramref name="window"/> of its start).</summary>
    public bool IsOwnEcho(string textHash, DateTime now, TimeSpan window)
        => SendingStartedAt is { } started && SendingTextHash == textHash && UtcTime.Normalize(now) - started <= window;

    /// <summary>The customer asked for a person: the timer fires within <paramref name="delay"/>, never later than it already would.</summary>
    public void Hasten(DateTime now, TimeSpan delay)
    {
        var soon = UtcTime.Normalize(now) + delay;
        if (State == AssistantState.Waiting && DueAt is { } due && soon < due)
        {
            DueAt = soon;
        }
    }

    public static ConversationAssistant Create(Guid tenantId, Guid conversationId, bool isSandbox)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria."),
            ConversationId = TextRules.RequiredId(conversationId, "La conversación es obligatoria."),
            IsSandbox = isSandbox,
        };

    /// <summary>The timer is armed and nobody holds it, or the holder's claim expired.</summary>
    public bool IsDue(DateTime now)
        => State == AssistantState.Waiting && DueAt is { } due && due <= now && (ClaimedUntil is not { } held || held <= now);

    /// <summary>An instance is preparing an answer right now (the inbox shows "respondiendo IA").</summary>
    public bool IsGenerating(DateTime now) => State == AssistantState.Waiting && ClaimedUntil is { } held && held > now;

    /// <summary>
    /// An unanswered customer message arrived. Arms the timer unless the conversation is pending human review or held by a person (a hold
    /// that ended releases it first). A timer already armed keeps its due time (the customer waits since the first unanswered message),
    /// but the generation moves: an answer in preparation did not read this message. Returns whether the timer is armed now.
    /// </summary>
    public bool CustomerWrote(DateTime now, TimeSpan delay)
    {
        now = UtcTime.Normalize(now);
        switch (State)
        {
            case AssistantState.HandedOff:
                return false;
            case AssistantState.HumanTookOver when HumanHoldUntil is not { } until || now < until:
                return false;
            case AssistantState.HumanTookOver:
                // The hold after a person wrote is over: the assistant starts again, as a new episode.
                ResetEpisode();
                break;
            case AssistantState.Waiting:
                Generation++;
                return true;
        }

        State = AssistantState.Waiting;
        DueAt = now + delay;
        ArmedAt = now;
        ClaimedUntil = null;
        Attempts = 0;
        Generation++;
        return true;
    }

    /// <summary>A person wrote (from Conversia or the phone app): the timer is cancelled and the assistant holds back for <paramref name="cooldown"/>.</summary>
    public void HumanWrote(DateTime now, TimeSpan cooldown)
    {
        now = UtcTime.Normalize(now);
        var explicitHold = State == AssistantState.HumanTookOver && HumanHoldUntil is null;
        State = AssistantState.HumanTookOver;
        HumanTookOverAt = now;
        HumanHoldUntil = explicitHold ? null : now + cooldown;
        HandoffReason = null;
        StopTimer();
    }

    /// <summary>A person takes the conversation over from the conversation view: the assistant stays quiet until it is handed back.</summary>
    public void TakeOver(Guid userId, DateTime now)
    {
        TakenOverBy = TextRules.RequiredId(userId, "La persona que toma la conversación es obligatoria.");
        State = AssistantState.HumanTookOver;
        HumanTookOverAt = UtcTime.Normalize(now);
        HumanHoldUntil = null;
        HandoffReason = null;
        StopTimer();
    }

    /// <summary>
    /// A person gives the conversation back to the assistant: counters start again. With <paramref name="armAfter"/> (the customer is waiting
    /// for a reply) the timer is armed at once with that delay; otherwise the assistant waits for the next customer message.
    /// </summary>
    public bool HandBack(DateTime now, TimeSpan? armAfter)
    {
        if (State is not (AssistantState.HumanTookOver or AssistantState.HandedOff))
        {
            return false;
        }

        now = UtcTime.Normalize(now);
        ResetEpisode();
        StopTimer();
        if (armAfter is { } delay)
        {
            State = AssistantState.Waiting;
            DueAt = now + delay;
            ArmedAt = now;
            Attempts = 0;
        }
        else
        {
            State = AssistantState.Off;
        }

        return true;
    }

    /// <summary>Claims a due timer for <paramref name="lease"/> (the job does the same in SQL). False when it is not due or another holder has it.</summary>
    public bool TryClaim(DateTime now, TimeSpan lease)
    {
        now = UtcTime.Normalize(now);
        if (!IsDue(now))
        {
            return false;
        }

        ClaimedUntil = now + lease;
        Attempts++;
        Generation++; // a holder whose lease expired is stale from now on: it can never send
        return true;
    }

    /// <summary>The timer the job claimed with <paramref name="generation"/> is still the one to answer.</summary>
    public bool IsCurrent(int generation) => State == AssistantState.Waiting && Generation == generation;

    /// <summary>The assistant's reply was sent and recorded.</summary>
    public void RecordReply(DateTime now, bool disclosed)
    {
        if (State != AssistantState.Waiting)
        {
            throw new DomainException("El asistente solo responde una conversación que espera su respuesta.");
        }

        now = UtcTime.Normalize(now);
        State = AssistantState.Active;
        AssistantTurns++;
        LastRepliedAt = now;
        if (disclosed)
        {
            DisclosedAt ??= now;
        }

        DueAt = null;
        ArmedAt = null;
        ClaimedUntil = null;
        ClearSending();
    }

    /// <summary>The assistant steps back: the conversation is pending human review with <paramref name="reason"/> (a code).</summary>
    public void HandOff(string reason, DateTime now)
    {
        HandoffReason = RequiredReason(reason);
        State = AssistantState.HandedOff;
        HandedOffAt = UtcTime.Normalize(now);
        StopTimer();
    }

    /// <summary>The timer fired but nothing is to be done (no longer eligible): back to rest, as if it had never been armed.</summary>
    public void Discard()
    {
        if (State != AssistantState.Waiting)
        {
            return;
        }

        State = AssistantTurns > 0 ? AssistantState.Active : AssistantState.Off;
        StopTimer();
    }

    /// <summary>The claim is given up without a decision (the answer was stale): the timer stays armed and is claimed again at once.</summary>
    public void ReleaseClaim() => ClaimedUntil = null;

    /// <summary>The generator could not understand the customer.</summary>
    public void RecordMisunderstanding() => MisunderstandingCount++;

    /// <summary>A reply that understood the customer: the misunderstandings that count are consecutive ones.</summary>
    public void ClearMisunderstandings() => MisunderstandingCount = 0;

    /// <summary>The reply just recorded redirected an off-topic customer (<paramref name="redirect"/>) or answered on topic (the run of redirects ends).</summary>
    public void RecordTopic(bool redirect) => OffTopicCount = redirect ? OffTopicCount + 1 : 0;

    private void ClearSending()
    {
        SendingStartedAt = null;
        SendingTextHash = null;
    }

    private void StopTimer()
    {
        ClearSending();
        DueAt = null;
        ArmedAt = null;
        ClaimedUntil = null;
        Generation++;
    }

    private void ResetEpisode()
    {
        AssistantTurns = 0;
        MisunderstandingCount = 0;
        OffTopicCount = 0;
        HandoffReason = null;
        HumanHoldUntil = null;
        TakenOverBy = null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ReasonShape();

    internal static string RequiredReason(string? reason)
        => reason is { Length: > 0 and <= HandoffReasonMaxLength } && ReasonShape().IsMatch(reason)
            ? reason
            : throw new DomainException("El motivo del traspaso no es válido.");
}

/// <summary>The stable codes of a handoff (the Web words them; counted per day for the administrator's monitoring).</summary>
public static class AssistantHandoffReasons
{
    /// <summary>The assistant answered as many times as the company allows in a row.</summary>
    public const string MaxTurns = "max_turns";

    /// <summary>A customer message matched one of the company's handoff keywords or patterns (complaint, legal threat, refund...).</summary>
    public const string Keyword = "keyword";

    /// <summary>The customer asked for a person.</summary>
    public const string HumanRequested = "human_requested";

    /// <summary>The generator did not understand the customer too many times.</summary>
    public const string Misunderstanding = "misunderstanding";

    /// <summary>The customer service window closed: only a template could be sent, and the assistant never sends one.</summary>
    public const string WindowClosed = "window_closed";

    /// <summary>A customer message looked like an attempt to steer the assistant (prompt injection).</summary>
    public const string InjectionSuspected = "injection_suspected";

    /// <summary>No reply generator is available (the default until the model is wired in).</summary>
    public const string AssistantUnavailable = "assistant_unavailable";

    /// <summary>The generator failed or answered something unusable.</summary>
    public const string GeneratorFailed = "generator_failed";

    /// <summary>The generator decided a person should take it.</summary>
    public const string GeneratorHandoff = "generator_handoff";

    /// <summary>The reply could not be sent (provider refusal).</summary>
    public const string SendFailed = "send_failed";

    /// <summary>The company used up what it may spend on the assistant this month (unit 5b meters it).</summary>
    public const string QuotaExhausted = "quota_exhausted";

    /// <summary>An earlier attempt started to send and its outcome is unknown (a crash or a timeout during the provider call): never sent twice.</summary>
    public const string SendUnknown = "send_unknown";

    /// <summary>The timer fired too long after its due time (the job was down): a person answers instead of a late assistant.</summary>
    public const string TooLate = "too_late";

    /// <summary>The timer failed too many times.</summary>
    public const string TimerFailed = "timer_failed";

    /// <summary>The customer sent audio or media without text, which the assistant cannot understand.</summary>
    public const string MediaNotUnderstood = "media_not_understood";

    /// <summary>The company's or the conversation's hourly ceiling of assistant messages was reached.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The customer kept writing about something outside the company's topics after a redirect (unit 5b guard).</summary>
    public const string OffTopic = "off_topic";

    /// <summary>The guard read a complaint in the customer's message (unit 5b).</summary>
    public const string Complaint = "complaint";

    /// <summary>The model's answer failed the reply checks twice (ungrounded facts, style, links): a person answers (unit 5b).</summary>
    public const string InvalidReply = "invalid_reply";

    /// <summary>The assistant closed the sale (the customer committed): a person confirms the booking or the payment (unit 5b).</summary>
    public const string SaleClosed = "sale_closed";

    /// <summary>The model declined to answer (a safety refusal): a person answers (unit 5b).</summary>
    public const string ModelRefused = "model_refused";

    public static readonly IReadOnlyList<string> All =
        [MaxTurns, Keyword, HumanRequested, Misunderstanding, WindowClosed, InjectionSuspected, AssistantUnavailable, GeneratorFailed, GeneratorHandoff, SendFailed, QuotaExhausted, SendUnknown, TooLate, TimerFailed, MediaNotUnderstood, RateLimited,
         OffTopic, Complaint, InvalidReply, SaleClosed, ModelRefused];
}
