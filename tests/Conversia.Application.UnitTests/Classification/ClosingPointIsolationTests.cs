using Conversia.Application.Classification;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>
/// The closing point's cues never change what the classifier already read: with the <c>closingPoint</c> section of the rules emptied or
/// loaded, the golden set and the generated corpus read the same outcome, loss reason, objection, service, reason, confidence and rule codes.
/// </summary>
public class ClosingPointIsolationTests
{
    private static readonly RuleBasedConversationClassifier Loaded = RuleBasedConversationClassifier.CreateDefault();

    private static readonly RuleBasedConversationClassifier Emptied = new(WithoutClosingPoint());

    private static ClassificationRuleSet WithoutClosingPoint()
    {
        var rules = ClassificationRuleSet.LoadDefault();
        rules.ClosingPoint = new ClassificationRuleSet.ClosingPointPhrases();
        return rules;
    }

    private static IEnumerable<string> Corpus()
    {
        foreach (var (_, chat, _) in ConversationClassifierGoldenSetTests.AllChats)
        {
            yield return chat;
        }

        var random = new Random(20261116);
        for (var i = 0; i < 4_000; i++)
        {
            yield return ClassifierGuaranteeTests.RandomChat(random, withAdvisor: i % 4 != 0);
        }

        // The closing cues themselves, in chats of both sides.
        foreach (var cue in new[] { "te llamo y cerramos", "pasa por la sede", "escríbeme al otro número", "ya hice la transferencia", "recibimos tu abono", "ya agendé por la página", "el valor es 60 mil", "más de 10 mil pacientes" })
        {
            yield return $"C: quiero agendar axilas\nA: {cue}\nC: {cue}";
            yield return $"C: precio?\nA: Quedó agendada para el sábado a las 10, {cue}\nC: no gracias, {cue}";
        }
    }

    [Fact]
    public void TheClosingCues_ChangeNothingElse()
    {
        var differences = new List<string>();
        foreach (var chat in Corpus())
        {
            var snapshot = ChatScript.Snapshot(chat);
            var a = Loaded.Classify(snapshot);
            var b = Emptied.Classify(snapshot);
            if ((a.Outcome, a.LossReason, a.Objection, a.ServiceInterest, a.InterestReason, a.Confidence) != (b.Outcome, b.LossReason, b.Objection, b.ServiceInterest, b.InterestReason, b.Confidence)
                || !a.RuleCodes.SequenceEqual(b.RuleCodes))
            {
                differences.Add(chat);
            }
        }

        Assert.Empty(differences);
    }

    [Fact]
    public void WithoutTheSection_NoCueIsRead()
    {
        var point = Emptied.Classify(ChatScript.Snapshot("C: precio de axilas?\nA: el valor es 60 mil\nA: te llamo y cerramos\nC: ya hice la transferencia")).ClosingPoint!;

        Assert.Equal((Domain.Enums.ReachedStage.NeedsIdentified, Domain.Enums.OffChannelMove.None), (point.ReachedStage!.Value, point.MovedOffChannel));
    }
}
