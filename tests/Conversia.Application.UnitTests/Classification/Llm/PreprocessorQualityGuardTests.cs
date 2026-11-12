using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Xunit.Abstractions;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>
/// Saving tokens must not lower quality: every phrase the rules classifier relies on (outcomes, loss reasons, services, interest reasons,
/// objections, confirmations, cancellations...) that appears in a conversation must still appear in what the model is sent. Checked over the
/// golden set and the generated corpus of <see cref="ClassifierGuaranteeTests"/>; messages left out by the token budget are reported, not
/// hidden. The test also measures the reduction (see docs/CLASSIFICATION.md, "Saving tokens").
/// </summary>
public class PreprocessorQualityGuardTests(ITestOutputHelper output)
{
    private static readonly ConversationPreprocessor Preprocessor = new(ConversationPreprocessorOptions.Default, ClassificationRuleSet.LoadDefault());

    // What the classifier reads in a business message (RuleBasedConversationClassifier.Analyze): services, proposals, days and hours,
    // confirmations and schedule statements. In a customer message it reads every phrase of the file.
    private static readonly HashSet<string> AdvisorSections =
    [
        "services.phrases", "generalServices.phrases", "bookingProposal", "proposalCue", "dayTime", "advisorConfirmation", "advisorScheduleStatement",
    ];

    // Every phrase of the rules file, normalized as the classifier normalizes text. A trailing '*' (hour prefixes) matches as a prefix.
    private static readonly (string Phrase, bool Prefix, bool Advisor)[] Signals = [.. RulesFile.Phrases()
        .Select(p => (Phrase: TextNormalizer.Normalize(p.Phrase.Trim().Trim('^', '$', ',', '*')), Prefix: p.Phrase.Trim().EndsWith('*'), Advisor: AdvisorSections.Contains(p.Path)))
        .Where(p => p.Phrase.Length > 0)
        .Distinct()];

    private static IEnumerable<(string Name, string Chat)> Corpus()
    {
        foreach (var (name, chat, _) in ConversationClassifierGoldenSetTests.AllChats)
        {
            yield return ("golden: " + name, chat);
        }

        var random = new Random(20261110);
        for (var i = 0; i < 3_000; i++)
        {
            yield return ($"generated #{i}", ClassifierGuaranteeTests.RandomChat(random, withAdvisor: true));
        }

        for (var i = 0; i < 1_000; i++)
        {
            yield return ($"generated customer-only #{i}", ClassifierGuaranteeTests.RandomChat(random, withAdvisor: false));
        }

        for (var i = 0; i < 200; i++)
        {
            yield return ($"long #{i}", LongChat(random));
        }
    }

    private const string Welcome = "¡Hola! 😊 Bienvenida a Láser Spa, especialistas en depilación láser. Cuéntanos, ¿qué zona te interesa depilar?";

    private const string PriceList = "Te comparto nuestros precios de la promoción de este mes: axilas 60 mil la sesión, bozo 45 mil, bikini 90 mil, "
        + "piernas completas 200 mil, media pierna 120 mil, brazos 110 mil, espalda 150 mil y cuerpo completo 650 mil. Los paquetes de 6 sesiones "
        + "tienen 20% de descuento y puedes pagar con tarjeta, transferencia o efectivo en cualquiera de nuestras sedes. ¿Te gustaría agendar una valoración gratuita?";

    // A synthetic long chat with the habits of real ones: a welcome and a price list sent again and again, customer bursts, stretched letters,
    // emoji, media without caption and links. Built only from the golden and generator lines, so every phrase is synthetic.
    private static string LongChat(Random random)
    {
        var script = new System.Text.StringBuilder("A: " + Welcome + "\n");
        for (var m = random.Next(30, 90); m > 0; m--)
        {
            var line = random.Next(10) switch
            {
                0 => "A: " + PriceList,
                1 => "C: [image]",
                2 => "C: siiiiiii 😍😍😍😍😍",
                3 => "A: Mira la promo aquí https://example.com/promo?utm_source=instagram&utm_campaign=laser",
                4 => "A: " + Welcome,
                5 or 6 => "A: " + ClassifierGuaranteeTests.Compose(random, ClassifierGuaranteeTests.AdvisorLines),
                _ => "C: " + ClassifierGuaranteeTests.Compose(random, ClassifierGuaranteeTests.CustomerLines),
            };
            script.Append(line).Append('\n');
        }

        return script.ToString();
    }

    private static HashSet<string> Present(string text, bool advisor = false)
    {
        var normalized = " " + TextNormalizer.Normalize(text) + " ";
        return [.. Signals.Where(s => (!advisor || s.Advisor) && normalized.Contains(" " + s.Phrase + (s.Prefix ? string.Empty : " "), StringComparison.Ordinal)).Select(s => s.Phrase)];
    }

    // What the block shows for each message number, with back-references (=n) resolved to the message they point to.
    private static Dictionary<int, string> Shown(PreprocessedConversation processed)
    {
        var shown = processed.Entries.OfType<PromptLine>().SelectMany(l => l.Segments).ToDictionary(s => s.Number, s => s.Text);
        foreach (var (number, text) in shown.ToList())
        {
            if (text.Length > 1 && text[0] == '=' && int.TryParse(text.AsSpan(1), out var target) && shown.TryGetValue(target, out var original))
            {
                shown[number] = original;
            }
        }

        return shown;
    }

    [Fact]
    public void EverySignalOfTheRules_SurvivesThePreprocessing_InTheSameMessage()
    {
        // Message by message: a phrase lost from one message fails even if the same phrase survives in another one. The comparison is
        // pseudonymized-before against pseudonymized-after, so only the preprocessor is judged here.
        var lost = new List<string>();
        var budgetReports = new List<string>();
        foreach (var (name, chat) in Corpus())
        {
            var masked = Pseudonymizer.Pseudonymize(ChatScript.Snapshot(chat));
            var processed = Preprocessor.Process(masked);
            var omitted = processed.Stats.OmittedIndices.ToHashSet();
            var shown = Shown(processed);

            for (var i = 0; i < masked.Messages.Count; i++)
            {
                var message = masked.Messages[i];
                var text = message.Text ?? string.Empty;
                text = text.Length > RuleBasedConversationClassifier.MaxCharactersPerMessage ? text[..RuleBasedConversationClassifier.MaxCharactersPerMessage] : text;
                var before = Present(text, advisor: message.Direction == Conversia.Domain.Enums.MessageDirection.Outbound);
                if (before.Count == 0)
                {
                    continue;
                }

                if (omitted.Contains(i + 1))
                {
                    budgetReports.Add($"{name} #{i + 1}: {string.Join(", ", before)}");
                    continue;
                }

                var after = shown.TryGetValue(i + 1, out var segment) ? Present(segment) : [];
                var missing = before.Except(after).ToList();
                if (missing.Count > 0)
                {
                    lost.Add($"{name} #{i + 1}: {string.Join(", ", missing)}");
                }
            }
        }

        output.WriteLine($"Messages whose signals the budget cap left out: {budgetReports.Count}");
        foreach (var report in budgetReports.Take(20))
        {
            output.WriteLine("  " + report);
        }

        Assert.Empty(lost);
    }

    [Fact]
    public void TheReduction_IsMeasuredAndReported()
    {
        long charsBefore = 0, charsAfter = 0, tokensBefore = 0, tokensAfter = 0, goldenBefore = 0, goldenAfter = 0, goldenTokensBefore = 0, goldenTokensAfter = 0;
        long longBefore = 0, longAfter = 0, longTokensBefore = 0, longTokensAfter = 0;
        var count = 0;
        var golden = 0;
        var longChats = 0;
        foreach (var (name, chat) in Corpus())
        {
            var stats = Preprocessor.Process(Pseudonymizer.Pseudonymize(ChatScript.Snapshot(chat))).Stats;
            count++;
            charsBefore += stats.CharactersBefore;
            charsAfter += stats.CharactersAfter;
            tokensBefore += stats.EstimatedTokensBefore;
            tokensAfter += stats.EstimatedTokensAfter;
            if (name.StartsWith("golden", StringComparison.Ordinal))
            {
                golden++;
                goldenBefore += stats.CharactersBefore;
                goldenAfter += stats.CharactersAfter;
                goldenTokensBefore += stats.EstimatedTokensBefore;
                goldenTokensAfter += stats.EstimatedTokensAfter;
            }

            if (name.StartsWith("long", StringComparison.Ordinal))
            {
                longChats++;
                longBefore += stats.CharactersBefore;
                longAfter += stats.CharactersAfter;
                longTokensBefore += stats.EstimatedTokensBefore;
                longTokensAfter += stats.EstimatedTokensAfter;
            }
        }

        static string Percent(long before, long after) => $"{100.0 * (before - after) / before:F1}%";

        output.WriteLine($"Corpus: {count} conversations ({golden} golden, {longChats} long synthetic).");
        output.WriteLine($"All: avg chars {charsBefore / (double)count:F1} -> {charsAfter / (double)count:F1} ({Percent(charsBefore, charsAfter)}), avg est. tokens {tokensBefore / (double)count:F1} -> {tokensAfter / (double)count:F1} ({Percent(tokensBefore, tokensAfter)})");
        output.WriteLine($"Golden: avg chars {goldenBefore / (double)golden:F1} -> {goldenAfter / (double)golden:F1} ({Percent(goldenBefore, goldenAfter)}), avg est. tokens {goldenTokensBefore / (double)golden:F1} -> {goldenTokensAfter / (double)golden:F1} ({Percent(goldenTokensBefore, goldenTokensAfter)})");

        output.WriteLine($"Long synthetic: avg chars {longBefore / (double)longChats:F1} -> {longAfter / (double)longChats:F1} ({Percent(longBefore, longAfter)}), avg est. tokens {longTokensBefore / (double)longChats:F1} -> {longTokensAfter / (double)longChats:F1} ({Percent(longTokensBefore, longTokensAfter)})");

        Assert.True(charsAfter < charsBefore);
        Assert.True(longAfter < longBefore);
        Assert.True(goldenAfter < goldenBefore);
    }
}
