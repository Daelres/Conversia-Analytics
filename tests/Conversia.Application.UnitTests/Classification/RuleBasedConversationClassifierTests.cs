using System.Text;
using Conversia.Application.Classification;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>Behaviors of the rule-based classifier beyond the golden set: negation, order, the shape of the output, and the dictionaries as data.</summary>
public class RuleBasedConversationClassifierTests
{
    private static readonly RuleBasedConversationClassifier Classifier = RuleBasedConversationClassifier.CreateDefault();

    private static ClassificationResult Read(string script) => Classifier.Classify(ChatScript.Snapshot(script));

    // ---- Negation -----------------------------------------------------------------------------

    [Theory]
    [InlineData("C: No me parece caro", ObjectionType.None)]
    [InlineData("C: No es caro", ObjectionType.None)]
    [InlineData("C: Nunca me ha parecido caro", ObjectionType.None)]
    [InlineData("C: Tampoco es caro", ObjectionType.None)]
    [InlineData("C: Es caro", ObjectionType.Price)]
    [InlineData("C: Me parece caro, pero lo pienso", ObjectionType.Price)] // two in one message: the rules' order decides (price first)
    [InlineData("C: No me parece caro, pero lo pienso", ObjectionType.Timing)]
    [InlineData("C: No me ha parecido caro", ObjectionType.None)]
    [InlineData("C: No, es caro", ObjectionType.Price)] // the comma ends the clause: the "no" answers something else
    [InlineData("C: Sin dolor? Me interesa", ObjectionType.None)]
    [InlineData("C: No duele", ObjectionType.None)]
    [InlineData("C: Duele", ObjectionType.Pain)]
    public void ANegatedObjection_IsNotAnObjection(string chat, ObjectionType expected)
        => Assert.Equal(expected, Read(chat).Objection);

    [Theory]
    [InlineData("C: No quiero agendar")]
    [InlineData("C: No voy a agendar todavía")]
    [InlineData("C: Nunca agendo nada")]
    public void ANegatedBookingIntent_IsNeverIntent_AndNeverWon(string chat)
    {
        var result = Read(chat + "\nA: Listo, quedó agendada tu cita para el sábado, te esperamos");

        Assert.NotEqual(ClassificationOutcome.Won, result.Outcome);
        Assert.DoesNotContain("intent.booking", result.RuleCodes);
        Assert.Contains("intent.negated", result.RuleCodes);
    }

    [Fact]
    public void ANegationFarFromThePhrase_DoesNotReachIt()
    {
        // "no" is more than four words before "agendar", in the same clause: the customer does want to book.
        var result = Read("C: No sé muy bien cómo funciona pero quiero agendar axilas");

        Assert.Contains("intent.booking", result.RuleCodes);
    }

    [Fact]
    public void ANegationInAnotherClause_DoesNotReachThePhrase()
    {
        Assert.Contains("intent.booking", Read("C: No, quiero agendar").RuleCodes);
        Assert.Contains("intent.booking", Read("C: Eso no. Quiero agendar").RuleCodes);
    }

    // ---- Won needs both halves ----------------------------------------------------------------

    [Fact]
    public void AnAdvisorConfirmation_WithoutCustomerIntent_IsNotWon()
        => Assert.NotEqual(ClassificationOutcome.Won, Read("C: Hola cuánto vale axilas\nA: 60 mil. Quedó agendada tu cita para mañana").Outcome);

    [Fact]
    public void CustomerIntent_WithoutAConfirmation_IsNotWon()
        => Assert.Equal(ClassificationOutcome.InFollowUp, Read("C: Quiero agendar axilas\nA: Claro, ¿qué día te sirve?\nC: El sábado").Outcome);

    [Fact]
    public void AConfirmationBeforeTheIntent_IsNotEnough()
        => Assert.NotEqual(ClassificationOutcome.Won, Read("A: Tu cita quedó agendada para mañana\nC: Hola, quiero agendar").Outcome);

    [Fact]
    public void AWon_ByTheAdvisor_IsMoreConfidentThanOneByTheCustomerAlone()
    {
        var byAdvisor = Read("C: Quiero agendar axilas\nA: Quedó agendada para el sábado");
        var byCustomer = Read("C: info axilas\nA: ¿Te agendo el sábado?\nC: Listo, confirmo");

        Assert.Equal((ClassificationOutcome.Won, ClassificationOutcome.Won), (byAdvisor.Outcome, byCustomer.Outcome));
        Assert.True(byAdvisor.Confidence > byCustomer.Confidence);
        Assert.Contains("outcome.won.advisor_confirmation", byAdvisor.RuleCodes);
        Assert.Contains("outcome.won.customer_confirmation", byCustomer.RuleCodes);
    }

    [Fact]
    public void ARefusalAfterTheBooking_TurnsItIntoLost_AndARequestToBookAfterARefusalUndoesIt()
    {
        Assert.Equal(ClassificationOutcome.Lost, Read("C: Quiero agendar axilas\nA: Quedó agendada para el sábado\nC: Mejor no, gracias").Outcome);
        Assert.Equal(ClassificationOutcome.InFollowUp, Read("C: No me interesa\nA: ¿Segura? Hay promo\nC: Bueno, quiero agendar").Outcome);
    }

    [Fact]
    public void AMessageThatCancels_IsNotARequestToBook_EvenThoughItSaysCita()
    {
        var result = Read("C: Quiero agendar axilas\nA: Quedó agendada para el sábado\nC: Necesito cancelar mi cita");

        Assert.Equal(ClassificationOutcome.Lost, result.Outcome);
        Assert.Contains("outcome.lost.cancellation", result.RuleCodes);
    }

    // ---- The shape of the result --------------------------------------------------------------

    [Fact]
    public void UndeterminedMeansLowConfidence_AndLostWithinTheRange()
    {
        Assert.True(Read("C: Hola").Confidence <= 0.3m);
        Assert.InRange(Read("C: No me interesa").Confidence, 0.5m, 1m);
    }

    [Fact]
    public void TheServicesAreLimitedToFive_AndStayWithin100Characters()
    {
        var result = Read("C: Cuánto cuesta axilas, piernas, bikini, rostro, bozo, brazos, espalda, abdomen, pecho y glúteos?");

        var services = result.ServiceInterest!.Split(',');
        Assert.Equal(5, services.Length);
        Assert.Equal(["axilas", "piernas", "bikini", "rostro", "bozo"], services);
        Assert.True(result.ServiceInterest.Length <= 100);
    }

    [Fact]
    public void WithoutAnyCustomerMention_TheServiceTheAdvisorNamesIsUsed()
    {
        var result = Read("C: Hola, info por favor\nA: Hola! Tenemos promo en axilas y piernas");

        Assert.Equal("axilas,piernas", result.ServiceInterest);
        Assert.Contains("service.from_advisor.axilas", result.RuleCodes);
    }

    [Fact]
    public void TheCustomersZone_WinsOverWhatTheAdvisorOffers()
    {
        Assert.Equal("bikini", Read("C: Cuánto vale el bikini\nA: También tenemos axilas y piernas").ServiceInterest);
    }

    [Fact]
    public void TheMostMentionedReason_Wins_AndTiesGoToTheFirst()
    {
        Assert.Equal("estetica", Read("C: Quiero verme mejor.\nC: Quiero verme bien y tener piel suave.\nC: Vi una promo").InterestReason);
        Assert.Equal("precio_promocion", Read("C: Vi la promo de las piernas\nC: Es por la boda").InterestReason);
    }

    [Fact]
    public void TheLatestObjection_IsTheOneReported()
    {
        Assert.Equal(ObjectionType.Timing, Read("C: Es muy caro\nA: Hay plan de pagos\nC: Mejor lo pienso").Objection);
    }

    [Fact]
    public void Classifying_DoesNotChangeTheSnapshot_AndIsTheSameInEveryThread()
    {
        var snapshot = ChatScript.Snapshot("C: Hola quiero agendar axilas\nA: ¿Te agendo el sábado?\nC: Listo, confirmo");
        var before = snapshot.Messages.Select(m => m.Text).ToList();

        var results = Enumerable.Range(0, 200).AsParallel().Select(_ => Classifier.Classify(snapshot)).ToList();

        Assert.Equal(before, snapshot.Messages.Select(m => m.Text));
        Assert.All(results, r => Assert.Equal(ClassificationOutcome.Won, r.Outcome));
        Assert.Single(results.Select(r => string.Join(',', r.RuleCodes)).Distinct());
    }

    [Fact]
    public void AVeryLongConversation_IsClassifiedWithoutTrouble()
    {
        var script = new StringBuilder("C: Hola quiero agendar axilas\n");
        for (var i = 0; i < 1500; i++)
        {
            script.Append(i % 2 == 0 ? "A: Seguimos atentos a tu respuesta\n" : "C: Estoy viendo gracias\n");
        }

        Assert.Equal(ClassificationOutcome.InFollowUp, Read(script.ToString()).Outcome);
    }

    [Fact]
    public async Task TheAsyncPort_GivesTheSameAnswerAsTheSyncMethod()
    {
        var snapshot = ChatScript.Snapshot("C: No me interesa");

        var viaPort = await ((IConversationClassifier)Classifier).ClassifyAsync(snapshot);

        Assert.Equal(Classifier.Classify(snapshot).RuleCodes, viaPort.RuleCodes);
    }

    // ---- The dictionaries are data ------------------------------------------------------------

    [Fact]
    public void TheDefaultRules_LoadAndValidate()
    {
        var rules = ClassificationRuleSet.LoadDefault();

        Assert.Equal(("es-CO", "laser-hair-removal", 1), (rules.Locale, rules.Vertical, rules.Version));
        Assert.Contains(rules.Services, s => s.Code == "axilas");
        Assert.NotEmpty(rules.Objections.Price);
    }

    private static Stream Json(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AnotherVertical_CanChangeTheDictionariesWithoutTouchingTheEngine()
    {
        // A dental clinic: its own services, its own words for booking and refusing.
        var dental = """
            {
              "version": 1,
              "negators": [ "no" ],
              "services": [ { "code": "limpieza", "phrases": [ "limpieza*", "profilaxis" ] }, { "code": "blanqueamiento", "phrases": [ "blanqueamiento" ] } ],
              "bookingIntent": [ "turno", "agendar" ],
              "customerConfirmation": { "strong": [ "confirmo mi turno" ], "weakAck": [ "^ok$" ] },
              "advisorConfirmation": [ "turno confirmado" ],
              "refusal": [ "paso", "no gracias" ],
              "inquiry": [ "precio" ]
            }
            """;
        var classifier = new RuleBasedConversationClassifier(ClassificationRuleSet.Parse(Json(dental), "the dental rules"));

        var won = classifier.Classify(ChatScript.Snapshot("C: Quiero un turno para limpieza dental\nA: Turno confirmado para el martes"));
        var lost = classifier.Classify(ChatScript.Snapshot("C: Precio del blanqueamiento?\nC: Paso, gracias"));
        var notDental = classifier.Classify(ChatScript.Snapshot("C: Cuánto cuesta depilar axilas"));

        Assert.Equal((ClassificationOutcome.Won, "limpieza"), (won.Outcome, won.ServiceInterest));
        Assert.Equal((ClassificationOutcome.Lost, "blanqueamiento", LossReason.NotInterested), (lost.Outcome, lost.ServiceInterest, lost.LossReason));
        Assert.Null(notDental.ServiceInterest); // the laser vocabulary is gone with the default rules
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{ \"version\": 2 }")]
    [InlineData("{ \"version\": 1, \"unknownSection\": [] }")]
    [InlineData("{ \"version\": 1, \"negators\": [\"no\"] }")]
    public void ABrokenRulesFile_IsRefusedWithAMessageThatQuotesNoContent(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClassificationRuleSet.Parse(Json(json), "the rules file"));

        Assert.StartsWith("The rules file", ex.Message);
        Assert.DoesNotContain("not json at all", ex.Message);
    }

    [Fact]
    public void RulesWithADuplicateOrMalformedCode_AreRefused()
    {
        var rules = ClassificationRuleSet.LoadDefault();
        rules.Services.Add(new ClassificationRuleSet.CodedPhrases { Code = "axilas", Phrases = ["otra"] });
        Assert.Throws<InvalidOperationException>(() => rules.Validate());

        var malformed = ClassificationRuleSet.LoadDefault();
        malformed.Services[0].Code = "Con Espacios";
        Assert.Throws<InvalidOperationException>(() => malformed.Validate());

        var blank = ClassificationRuleSet.LoadDefault();
        blank.Refusal.Add("   ");
        Assert.Throws<InvalidOperationException>(() => blank.Validate());
    }

    [Fact]
    public void ARulesFile_ThatDoesNotExist_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClassificationRuleSet.LoadFromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")));

        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void ARulesFile_OnDisk_Loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, File.ReadAllText(FindDefaultRulesFile()));

            var rules = ClassificationRuleSet.LoadFromFile(path);

            Assert.Equal("laser-hair-removal", rules.Vertical);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindDefaultRulesFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Conversia.Application", "Classification", "Rules", "ClassificationRules.es-CO.json");
    }
}
