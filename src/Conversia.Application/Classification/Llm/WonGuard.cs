namespace Conversia.Application.Classification.Llm;

/// <param name="Result">What is applied: the LLM reading, with the stage the guard allows.</param>
/// <param name="WonDowngraded">The LLM said Won and the rules did not read one: the rules outcome was applied instead.</param>
/// <param name="WonDisagreement">The LLM and the rules disagree about Won (either direction); recorded on the run, never shown as text.</param>
public sealed record WonGuardDecision(ClassificationResult Result, bool WonDowngraded, bool WonDisagreement);

/// <summary>
/// The Won guarantee of docs/CLASSIFICATION.md, kept outside the model (design D8). It takes the LLM reading and the reading of the
/// <b>existing</b> <see cref="RuleBasedConversationClassifier"/> over the <b>real</b> (not pseudonymized) snapshot, and gives the rules the
/// last word on Won in both directions:
/// <list type="bullet">
/// <item>rules Won: the stage is Won whatever the LLM said (a courtesy "no gracias" after a booking can never become Lost);</item>
/// <item>LLM Won without a rules Won: the Won is downgraded to the rules outcome (its stage and loss reason; Undetermined leaves the stage alone),
/// with <see cref="WonDowngradedCode"/> first in the rule codes so storing them within the column can never drop it;</item>
/// <item>otherwise the LLM stage stands.</item>
/// </list>
/// The descriptive fields (service, reason, objection, profile) always come from the LLM. Pure and deterministic.
/// </summary>
public static class WonGuard
{
    /// <summary>The rule code of a downgraded Won. Always the first code, ahead of the <c>llm.*</c> codes.</summary>
    public const string WonDowngradedCode = "guard.won_downgraded";

    /// <param name="llm">The mapped LLM reading.</param>
    /// <param name="rules">The rules reading of the same conversation, from its real text.</param>
    public static WonGuardDecision Apply(ClassificationResult llm, ClassificationResult rules)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(rules);

        var llmWon = llm.Outcome == ClassificationOutcome.Won;
        if (rules.Outcome == ClassificationOutcome.Won)
        {
            var won = llm with { Outcome = ClassificationOutcome.Won, LossReason = null, ClosingPoint = Booked(llm.ClosingPoint) };
            return new WonGuardDecision(Coherent(won), WonDowngraded: false, WonDisagreement: !llmWon);
        }

        if (llmWon)
        {
            var downgraded = llm with
            {
                Outcome = rules.Outcome,
                LossReason = rules.Outcome == ClassificationOutcome.Lost ? rules.LossReason : null,
                Confidence = rules.Confidence,
                WonEvidenceIndex = null,
                RuleCodes = [WonDowngradedCode, .. llm.RuleCodes.Where(c => c != WonDowngradedCode)],
                ClosingPoint = NotBooked(llm.ClosingPoint),
            };
            return new WonGuardDecision(Coherent(downgraded), WonDowngraded: true, WonDisagreement: true);
        }

        return new WonGuardDecision(Coherent(llm), WonDowngraded: false, WonDisagreement: false);
    }

    // The combination rules of the closing point (docs/CLASSIFICATION.md, "Closing point"), the same as the rules classifier's: a Won was
    // closed in the chat by the business; a Lost was not closed and did not continue anywhere; an open conversation keeps what was read.
    private static ClassificationResult Coherent(ClassificationResult result) => result.ClosingPoint is not { } point ? result : result.Outcome switch
    {
        ClassificationOutcome.Won => result with { ClosingPoint = point with { ClosedBy = Domain.Enums.ClosedBy.Advisor, ClosedOutsideSystem = false } },
        ClassificationOutcome.Lost => result with { ClosingPoint = point with { ClosedBy = Domain.Enums.ClosedBy.Unknown, ClosedOutsideSystem = false } },
        _ => result,
    };

    // A Won reached the booking (a payment step stays) and the business closed it.
    private static Domain.Entities.ClosingPointReading? Booked(Domain.Entities.ClosingPointReading? point)
        => point is null ? null : point with
        {
            ReachedStage = point.ReachedStage is { } stage && stage > Domain.Enums.ReachedStage.AppointmentBooked ? stage : Domain.Enums.ReachedStage.AppointmentBooked,
            ClosedBy = point.ClosedBy == Domain.Enums.ClosedBy.Unknown ? Domain.Enums.ClosedBy.Advisor : point.ClosedBy,
        };

    // A Won the rules did not confirm: the booking was not reached in the chat, and nobody closed it there. A deal the text shows continued
    // outside the chat keeps who closed it.
    private static Domain.Entities.ClosingPointReading? NotBooked(Domain.Entities.ClosingPointReading? point)
        => point is null ? null : point with
        {
            ReachedStage = point.ReachedStage == Domain.Enums.ReachedStage.AppointmentBooked ? Domain.Enums.ReachedStage.AppointmentProposed : point.ReachedStage,
            ClosedBy = point.ClosedOutsideSystem ? point.ClosedBy : Domain.Enums.ClosedBy.Unknown,
        };

    /// <summary>Runs the rules classifier over the real snapshot and applies the guard to the LLM reading.</summary>
    public static WonGuardDecision Apply(ClassificationResult llm, ConversationSnapshot realSnapshot, RuleBasedConversationClassifier rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return Apply(llm, rules.Classify(realSnapshot, cancellationToken));
    }
}
