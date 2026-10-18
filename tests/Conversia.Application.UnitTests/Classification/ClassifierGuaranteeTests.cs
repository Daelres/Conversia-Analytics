using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>
/// The guarantee of docs/CLASSIFICATION.md, checked as properties instead of as a list of cases: <b>a Won needs the business to say so</b>.
/// The customer's words alone never make one, whatever they are and in whatever order, and a courtesy after the booking never unmakes it.
/// The properties run over generated chats built from every phrase of the rules file (so a new phrase is attacked the day it is added),
/// with a fixed seed so a failure is the same failure everywhere.
/// </summary>
public class ClassifierGuaranteeTests
{
    private static readonly RuleBasedConversationClassifier Classifier = RuleBasedConversationClassifier.CreateDefault();

    private static ClassificationResult Read(string script) => Classifier.Classify(ChatScript.Snapshot(script));

    private static string Plain(string phrase) => phrase.Trim().Trim('^', '$', ',', '*').Trim();

    // Everything a customer could say according to the rules, plus the customer lines of the golden set.
    private static readonly string[] CustomerLines =
    [
        .. RulesFile.Phrases().Select(p => Plain(p.Phrase)).Where(p => p.Length > 0).Distinct(),
        .. ConversationClassifierGoldenSetTests.AllChats
            .SelectMany(c => c.Chat.Split('\n'))
            .Where(l => l.StartsWith("C:", StringComparison.Ordinal))
            .Select(l => l[2..].Trim()),
        "Hola", "Gracias", "Quiero agendar", "¿Mi cita está confirmada?", "Confirmo, ahí estaré", "Listo el sábado a las 3", "Sí", "Dale", "Quedamos así",
    ];

    // What the business says: its own confirmations and proposals, and filler.
    private static readonly string[] AdvisorLines =
    [
        .. RulesFile.Phrases().Where(p => p.Path is "advisorConfirmation" or "advisorScheduleStatement" or "bookingProposal" or "proposalCue" or "dayTime").Select(p => Plain(p.Phrase)),
        .. ConversationClassifierGoldenSetTests.AllChats
            .SelectMany(c => c.Chat.Split('\n'))
            .Where(l => l.StartsWith("A:", StringComparison.Ordinal))
            .Select(l => l[2..].Trim()),
        "Hola, bienvenida", "¿En qué te ayudo?", "Con gusto", "60 mil", "Tu cita quedó agendada para el sábado a las 10 am",
    ];

    private static readonly string[] Joiners = [". ", ", ", "? ", " ", "! ", " pero "];

    private static string Compose(Random random, string[] lines)
    {
        var count = random.Next(1, 4);
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(lines[random.Next(lines.Length)]);
            if (i < count - 1)
            {
                text.Append(Joiners[random.Next(Joiners.Length)]);
            }
        }

        return text.ToString().Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string RandomChat(Random random, bool withAdvisor)
    {
        var script = new StringBuilder();
        var messages = random.Next(1, 8);
        for (var i = 0; i < messages; i++)
        {
            var advisor = withAdvisor && random.Next(3) == 0;
            script.Append(advisor ? "A: " : "C: ").Append(Compose(random, advisor ? AdvisorLines : CustomerLines)).Append('\n');
        }

        return script.ToString();
    }

    [Fact]
    public void ACustomerWhoIsAloneInTheChat_NeverMakesAWon()
    {
        var random = new Random(20261018);
        var wrong = new List<string>();
        for (var i = 0; i < 20_000; i++)
        {
            var chat = RandomChat(random, withAdvisor: false);
            if (Read(chat).Outcome == ClassificationOutcome.Won)
            {
                wrong.Add(chat);
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void WhateverTheCustomerSays_AnAdvisorWhoSaysNothingUseful_NeverMakesAWon()
    {
        // The business only writes filler: no proposal with a day, no confirmation. Then no sequence of customer messages is a sale.
        var random = new Random(7);
        var wrong = new List<string>();
        for (var i = 0; i < 20_000; i++)
        {
            var script = new StringBuilder();
            for (var m = random.Next(1, 8); m > 0; m--)
            {
                script.Append(random.Next(2) == 0 ? "A: Hola, ¿en qué te ayudo?\n" : "C: " + Compose(random, CustomerLines) + "\n");
            }

            if (Read(script.ToString()).Outcome == ClassificationOutcome.Won)
            {
                wrong.Add(script.ToString());
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryWonOfTheGeneratedChats_DependsOnWhatTheBusinessWrote()
    {
        // Metamorphic: take the chats that end as Won and replace every advisor message with filler. None may stay Won.
        var random = new Random(99);
        var won = 0;
        var wrong = new List<string>();
        for (var i = 0; i < 40_000; i++)
        {
            var chat = RandomChat(random, withAdvisor: true);
            if (Read(chat).Outcome != ClassificationOutcome.Won)
            {
                continue;
            }

            won++;
            var filler = string.Join('\n', chat.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.StartsWith("A:", StringComparison.Ordinal) ? "A: Hola, ¿en qué te ayudo?" : l));
            if (Read(filler).Outcome == ClassificationOutcome.Won)
            {
                wrong.Add(chat);
            }
        }

        Assert.True(won > 20, $"the generator produced only {won} Won chats: the property would prove nothing");
        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryGoldenChat_WithTheBusinessReplacedByFiller_IsNeverWon()
    {
        foreach (var (name, chat, _) in ConversationClassifierGoldenSetTests.AllChats)
        {
            var filler = string.Join('\n', chat.Split('\n').Select(l => l.StartsWith("A:", StringComparison.Ordinal) ? "A: Hola, ¿en qué te ayudo?" : l));

            Assert.True(Read(filler).Outcome != ClassificationOutcome.Won, name);
        }
    }

    [Theory]
    [InlineData("No gracias")]
    [InlineData("No")]
    [InlineData("Gracias, no")]
    [InlineData("Ok, gracias")]
    [InlineData("Todo bien")]
    [InlineData("Mejor no, gracias")]
    [InlineData("Ya cancelé el abono")]
    public void ACourtesyAfterAConfirmedBooking_NeverUnmakesIt(string courtesy)
    {
        // The golden chats that are Won because the business confirmed, and the generated ones: a "no gracias" at the end changes nothing.
        var random = new Random(31);
        var checkedChats = 0;
        var chats = ConversationClassifierGoldenSetTests.AllChats.Where(c => c.Outcome == ClassificationOutcome.Won).Select(c => c.Chat)
            .Concat(Enumerable.Range(0, 15_000).Select(_ => RandomChat(random, withAdvisor: true)));
        foreach (var chat in chats)
        {
            if (Read(chat).Outcome != ClassificationOutcome.Won)
            {
                continue;
            }

            checkedChats++;
            var after = Read(chat.TrimEnd('\n') + "\nA: ¿Algo más?\nC: " + courtesy + "\n");

            Assert.True(after.Outcome == ClassificationOutcome.Won, $"{courtesy} flipped:\n{chat}");
        }

        Assert.True(checkedChats > 20);
    }

    [Fact]
    public void AnExplicitCancellationAfterAConfirmedBooking_EndsIt()
    {
        foreach (var cancel in new[] { "Necesito cancelar mi cita", "Quiero cancelar la cita", "Ya no voy a poder ir", "No puedo ir", "Anular la reserva por favor" })
        {
            var result = Read("C: Quiero agendar axilas\nA: Quedó agendada para el sábado a las 10\nC: " + cancel);

            Assert.Equal(ClassificationOutcome.Lost, result.Outcome);
        }
    }

    // ---- Determinism: the order of the dictionaries must not matter -------------------------------------------------------------

    private static JsonNode ShuffledRules(int seed)
    {
        var random = new Random(seed);

        static void Shuffle(Random random, JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (_, value) in obj.ToList())
                    {
                        if (value is not null)
                        {
                            Shuffle(random, value);
                        }
                    }

                    break;
                case JsonArray array:
                    var items = array.ToList();
                    foreach (var item in items.Where(i => i is not null))
                    {
                        Shuffle(random, item!);
                    }

                    // Arrays of strings are sets of phrases; arrays of objects (services, reasons) keep their order: it is the order of the codes in a result.
                    if (items.All(i => i is JsonValue))
                    {
                        array.Clear();
                        foreach (var item in items.OrderBy(_ => random.Next()))
                        {
                            array.Add(item!.DeepClone());
                        }
                    }

                    break;
            }
        }

        var root = RulesFile.Read();
        Shuffle(random, root);
        if (root["aliases"] is JsonObject aliases)
        {
            var pairs = aliases.ToList().OrderBy(_ => random.Next()).Select(p => (p.Key, Value: p.Value!.DeepClone())).ToList();
            aliases.Clear();
            foreach (var (key, value) in pairs)
            {
                aliases.Add(key, value);
            }
        }

        return root;
    }

    private static RuleBasedConversationClassifier Shuffled(int seed)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ShuffledRules(seed).ToJsonString()));
        return new RuleBasedConversationClassifier(ClassificationRuleSet.Parse(stream, "the shuffled rules"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnotherInstanceOfTheSameRules_WithTheDictionariesInAnotherOrder_ReadsEveryChatTheSame(int seed)
    {
        var other = Shuffled(seed);
        var random = new Random(seed);
        var chats = ConversationClassifierGoldenSetTests.AllChats.Select(c => c.Chat)
            .Concat(Enumerable.Range(0, 3_000).Select(_ => RandomChat(random, withAdvisor: true)));

        foreach (var chat in chats)
        {
            var snapshot = ChatScript.Snapshot(chat);

            var a = Classifier.Classify(snapshot);
            var b = other.Classify(snapshot);

            Assert.Equal(
                (a.Outcome, a.Objection, a.LossReason, a.ServiceInterest, a.InterestReason, a.Confidence, string.Join(',', a.RuleCodes)),
                (b.Outcome, b.Objection, b.LossReason, b.ServiceInterest, b.InterestReason, b.Confidence, string.Join(',', b.RuleCodes)));
        }
    }

    [Fact]
    public void ShufflingTheRules_ReallyChangesTheirOrder_AndNothingElse()
    {
        // The determinism test above proves nothing if the shuffle is a no-op.
        var original = RulesFile.Read();
        var shuffled = ShuffledRules(1);

        foreach (var section in new[] { "refusal", "inquiry", "bookingIntent", "advisorConfirmation" })
        {
            var before = original[section]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var after = shuffled[section]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

            Assert.NotEqual(before, after);
            Assert.Equal(before.Order().ToList(), after.Order().ToList());
        }

        Assert.Equal(
            original["aliases"]!.AsObject().Select(p => p.Key).Order().ToList(),
            shuffled["aliases"]!.AsObject().Select(p => p.Key).Order().ToList());
        Assert.NotEqual(original["aliases"]!.AsObject().Select(p => p.Key).ToList(), shuffled["aliases"]!.AsObject().Select(p => p.Key).ToList());
    }

    // ---- Bounded work -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheLatestMessages_AreRead()
    {
        // A booking that happened more than MaxMessages ago, then a lot of chat: the old evidence is out of the window.
        var script = new StringBuilder("C: Quiero agendar axilas\nA: Quedó agendada para el sábado a las 10\n");
        for (var i = 0; i < RuleBasedConversationClassifier.MaxMessages; i++)
        {
            script.Append(i % 2 == 0 ? "C: Gracias por la información\n" : "A: Con gusto\n");
        }

        Assert.NotEqual(ClassificationOutcome.Won, Read(script.ToString()).Outcome);

        var within = new StringBuilder("C: Quiero agendar axilas\nA: Quedó agendada para el sábado a las 10\n");
        for (var i = 0; i < RuleBasedConversationClassifier.MaxMessages - 3; i++)
        {
            within.Append(i % 2 == 0 ? "C: Gracias por la información\n" : "A: Con gusto\n");
        }

        Assert.Equal(ClassificationOutcome.Won, Read(within.ToString()).Outcome);
    }

    [Fact]
    public void OnlyTheFirstCharactersOfAMessage_AreRead()
    {
        var filler = new string('a', RuleBasedConversationClassifier.MaxCharactersPerMessage) + " ";

        var late = Read("C: Cuánto cuesta axilas\nC: " + filler + "no me interesa");
        var early = Read("C: Cuánto cuesta axilas\nC: no me interesa " + filler);

        Assert.Equal(ClassificationOutcome.InFollowUp, late.Outcome);
        Assert.Equal(ClassificationOutcome.Lost, early.Outcome);
    }

    [Fact]
    public void AdversarialLongMessages_AreClassifiedQuicklyAndWithinTheBounds()
    {
        // 300 messages (more than the window) of the worst shapes: every phrase of the rules in a row, one endless sentence, endless clauses.
        var everyPhrase = string.Join(' ', RulesFile.Phrases().Select(p => Plain(p.Phrase)));
        var shapes = new[]
        {
            everyPhrase,
            string.Concat(Enumerable.Repeat("no ", 600)),
            string.Concat(Enumerable.Repeat("no me interesa, pero ", 100)),
            string.Concat(Enumerable.Repeat("¿cancelar la cita? ", 100)),
            string.Concat(Enumerable.Repeat("a las 10 ", 200)),
        };
        var messages = Enumerable.Range(0, 300)
            .Select(i => new SnapshotMessage(i % 2 == 0 ? MessageDirection.Inbound : MessageDirection.Outbound, "text", shapes[i % shapes.Length], DateTime.UnixEpoch.AddMinutes(i)))
            .ToList();
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), null, DateTime.UnixEpoch, DateTime.UnixEpoch, null, messages);

        var clock = Stopwatch.StartNew();
        var result = Classifier.Classify(snapshot);
        clock.Stop();

        Assert.InRange(result.Confidence, 0m, 1m);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}"); // generous: it takes well under a second; this catches a blow-up, not jitter
    }

    [Fact]
    public void ACancelledToken_StopsTheClassification()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Classifier.Classify(ChatScript.Snapshot("C: Hola\nA: Hola"), cts.Token));
    }

    [Fact]
    public async Task ACancellationInTheMiddleOfAConversation_StopsBeforeReadingTheRest()
    {
        using var cts = new CancellationTokenSource();
        var messages = new CancellingList(
            Enumerable.Range(0, 150).Select(i => new SnapshotMessage(MessageDirection.Inbound, "text", "Quiero agendar axilas", DateTime.UnixEpoch.AddMinutes(i))).ToList(),
            cancelAfterReading: 40,
            cts);
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), null, DateTime.UnixEpoch, DateTime.UnixEpoch, null, messages);

        await Assert.ThrowsAsync<OperationCanceledException>(() => ((IConversationClassifier)Classifier).ClassifyAsync(snapshot, cts.Token));

        Assert.True(messages.Reads < 60, $"{messages.Reads} messages were read after the cancellation");
    }

    private sealed class CancellingList(List<SnapshotMessage> inner, int cancelAfterReading, CancellationTokenSource source) : IReadOnlyList<SnapshotMessage>
    {
        public int Reads { get; private set; }

        public int Count => inner.Count;

        public SnapshotMessage this[int index]
        {
            get
            {
                if (++Reads == cancelAfterReading)
                {
                    source.Cancel();
                }

                return inner[index];
            }
        }

        public IEnumerator<SnapshotMessage> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- The rules file: no risky prefixes ------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultRules_UseNoWildcardOtherThanTheHourPrefixes()
    {
        // "ingle*" matched "ingles" (the language), "agend*" matched "agenda", "quince*" matched "quincena", "cupo*" matched "cupon".
        // A prefix is a bet about the whole language; the file spells every form out. The only exception is a digit stem ("a las 1*").
        var wildcards = RulesFile.Phrases().Where(p => p.Phrase.Contains('*')).ToList();

        Assert.All(wildcards, w =>
        {
            Assert.Equal("dayTime", w.Path);
            Assert.Matches(@"^a la[s]? \d\*$", w.Phrase);
        });
    }

    [Theory]
    [InlineData("C: Estudio inglés", "bikini")]
    [InlineData("C: La quincena está dura", "evento")]
    [InlineData("C: Revisé mi agenda", "intent.booking")]
    [InlineData("C: Tengo un cupón", "intent.booking")]
    public void WordsThatOnlyLookLikeADictionaryWord_FireNothing(string chat, string code)
    {
        var result = Read(chat);

        Assert.DoesNotContain(code, result.RuleCodes.Append(result.ServiceInterest).Append(result.InterestReason));
    }
}
