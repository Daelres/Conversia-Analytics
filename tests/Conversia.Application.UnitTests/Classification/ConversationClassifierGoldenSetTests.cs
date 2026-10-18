using Conversia.Application.Classification;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>
/// The golden set: realistic Spanish (Colombia) chat snippets of a laser hair removal business, with what the rule-based classifier must read
/// in each. It covers accents and their absence, caps, stretched letters, emoji, abbreviations, negations and ambiguous chats. The rule for
/// the ambiguous ones is the point of the product: <b>never a confident wrong Won</b> — when in doubt the answer is InFollowUp or
/// Undetermined. Every case also checks the invariants of any result (confidence in 0..1, codes only, no customer words in the output).
/// </summary>
public class ConversationClassifierGoldenSetTests
{
    private static readonly RuleBasedConversationClassifier Classifier = RuleBasedConversationClassifier.CreateDefault();

    private const ClassificationOutcome Undetermined = ClassificationOutcome.Undetermined;
    private const ClassificationOutcome Follow = ClassificationOutcome.InFollowUp;
    private const ClassificationOutcome Won = ClassificationOutcome.Won;
    private const ClassificationOutcome Lost = ClassificationOutcome.Lost;

    private sealed record Case(
        string Name,
        string Chat,
        ClassificationOutcome Outcome,
        ObjectionType Objection = ObjectionType.None,
        LossReason? Loss = null,
        string? Service = null,
        string? Reason = null);

    private static readonly Case[] Cases =
    [
        // ---- Nothing to read: greetings, media, one-sided chats ----------------------------------------------------------------
        new("greeting only", "C: Hola buenas tardes", Undetermined),
        new("greeting and welcome", "C: Hola\nA: Hola, bienvenida a Láser Spa, ¿en qué te ayudo?", Undetermined),
        new("thanks only", "C: Gracias", Undetermined),
        new("bare ok without context", "C: ok", Undetermined),
        new("customer sends only an audio", "C: [audio]", Undetermined),
        new("customer sends an image and the advisor asks", "C: [image]\nA: Hola, ¿qué zona te interesa?", Undetermined),
        new("only the business writes", "A: Hola, te escribimos de Láser Spa por tu valoración gratuita", Undetermined),
        new("advisor says it is booked but the customer never asked", "C: Hola\nA: Hola, te cuento que tu cita quedó agendada para mañana", Undetermined),

        // ---- Interest: services, zones and reasons -----------------------------------------------------------------------------
        new("price of armpits", "C: Hola, cuánto cuesta la depilación láser en axilas?", Follow, Service: "axilas"),
        new("general information", "C: Buenas, quisiera información de la depilación laser", Follow, Service: "depilacion_laser"),
        new("two zones", "C: Cuánto vale piernas completas y bikini?\nA: Piernas 180 mil y bikini 90 mil", Follow, Service: "piernas,bikini"),
        new("full body", "C: cuanto cuesta el cuerpo completo", Follow, Service: "cuerpo_completo"),
        new("full body replaces the zones", "C: quiero cuerpo completo, incluye axilas?", Follow, Service: "cuerpo_completo"),
        new("emoji and two zones", "C: 😍😍 quiero info de depilación láser en piernas y axilas 🙏", Follow, Service: "axilas,piernas"),
        new("face and moustache", "C: Buenas! 🙂 vi su publicidad. Cuánto vale la depilación en rostro y bozo?", Follow, Service: "rostro,bozo"),
        new("all caps", "C: HOLA, QUIERO AGENDAR UNA CITA PARA BIKINI", Follow, Service: "bikini"),
        new("stretched letters", "C: holaaaa, cuanto vale la depilacion de axilassss?", Follow, Service: "axilas"),
        new("no accents", "C: Cuanto cuesta la depilacion laser de bozo", Follow, Service: "bozo"),
        new("abbreviation of que", "C: q precio tiene el bikini brasilero", Follow, Service: "bikini"),
        new("a zone is denied", "C: No quiero axilas, solo piernas", Follow, Service: "piernas"),
        new("n with tilde in the text", "C: Mañana puedo ir?", Follow),
        new("promotion as the reason", "C: Vi la promo del 2x1 en Instagram, aún está vigente?", Follow, Reason: "precio_promocion"),
        new("comfort as the reason", "C: Estoy cansada de rasurarme, quiero dejar la cera, cuánto vale piernas?", Follow, Service: "piernas", Reason: "comodidad"),
        new("event as the reason", "C: Me caso en diciembre y quiero depilarme axilas y bikini antes de la boda", Follow, Service: "axilas,bikini", Reason: "evento"),
        new("health as the reason", "C: Me salen muchos vellos encarnados y foliculitis en las piernas", Follow, Service: "piernas", Reason: "salud"),
        new("recommendation as the reason", "C: Mi amiga se depiló con ustedes y quedó feliz, cuánto vale el bikini", Follow, Service: "bikini", Reason: "recomendacion"),
        new("discount question", "C: Hacen descuento si me hago cuerpo completo?", Follow, ObjectionType.Price, Service: "cuerpo_completo", Reason: "precio_promocion"),

        // ---- Objections: raised, and not raised (negation) -----------------------------------------------------------------
        new("price: too expensive", "C: cuánto vale rostro\nA: 80 mil por sesión\nC: uy está muy caro", Follow, ObjectionType.Price, Service: "rostro"),
        new("price: cannot afford", "C: Me encantaría pero no me alcanza el presupuesto este mes", Follow, ObjectionType.Price),
        new("price is not an objection when denied", "C: no me parece caro, quiero agendar para axilas", Follow, Service: "axilas"),
        new("price: cheaper elsewhere", "C: Mi amiga me dijo que en otro centro están más baratos", Follow, ObjectionType.Price),
        new("timing: I will think about it", "C: Lo voy a pensar y te aviso", Follow, ObjectionType.Timing),
        new("timing: later", "C: Después te escribo, gracias", Follow, ObjectionType.Timing),
        new("timing: not now", "C: más adelante, ahorita no puedo", Follow, ObjectionType.Timing),
        new("timing: next month", "C: Quiero agendar pero hasta el próximo mes", Follow, ObjectionType.Timing),
        new("timing: cannot come that day", "C: Ese sábado no puedo ir, tengo otro compromiso", Follow, ObjectionType.Timing),
        new("location: too far", "C: Y donde quedan? Uy eso me queda muy lejos", Follow, ObjectionType.Location),
        new("location question is not an objection", "C: Dónde queda la sede del centro?", Follow),
        new("pain", "C: Duele mucho? Le tengo miedo al dolor", Follow, ObjectionType.Pain),
        new("pain with a subjunctive", "C: No quiero que me duela mucho, hay anestesia?", Follow, ObjectionType.Pain),
        new("pain denied is not an objection", "C: Me dijeron que no duele, es cierto?", Follow),
        new("trust: registration", "C: Es seguro? Tienen registro Invima?", Follow, ObjectionType.Trust),
        new("trust: scam", "C: Y los resultados son reales? he escuchado que es una estafa", Follow, ObjectionType.Trust),
        new("other: pregnant", "C: Estoy embarazada, puedo hacerme axilas?", Follow, ObjectionType.Other, Service: "axilas"),
        new("price and booking intent together", "C: Quiero agendar pero está muy caro", Follow, ObjectionType.Price),

        // ---- Booking intent that is not a booking -------------------------------------------------------------------------------
        new("wants to book", "C: Quiero agendar una cita para piernas", Follow, Service: "piernas"),
        new("accepts a proposal, nobody confirms", "C: cuanto cuestan las axilas\nA: 60 mil. ¿Te agendo?\nC: dale", Follow, Service: "axilas"),
        new("dale without a proposal", "C: cuanto cuestan las piernas\nA: 180 mil, incluye 6 sesiones\nC: dale", Follow, Service: "piernas"),
        new("listo gracias after a price", "C: cuanto cuesta bikini\nA: 90 mil\nC: listo gracias", Follow, Service: "bikini"),
        new("confirmo with no booking talk before", "C: Confirmo", Follow),
        new("a question is not a confirmation", "C: Quiero agendar piernas\nA: ¿Te quedó agendada con la recepcionista?", Follow, Service: "piernas"),
        new("a negated confirmation", "C: Quiero cita\nA: Todavía no quedó agendada, ¿qué día prefieres?", Follow),
        new("no quiero agendar", "C: No quiero agendar todavía, solo estoy cotizando", Follow),
        new("ya no with a yes after", "C: Ya no tengo dudas, quiero agendar", Follow),

        // ---- Won: intent plus confirmation --------------------------------------------------------------------------------------
        new("won: advisor confirms", "C: Quiero agendar cita para axilas\nA: Claro, ¿el sábado a las 10?\nC: Sí, perfecto\nA: Listo, quedó agendada tu cita para el sábado a las 10 am. Te esperamos", Won, Service: "axilas"),
        new("won: customer confirms a proposal", "C: Hola, info de bikini\nA: $90.000. ¿Te agendo el sábado a las 3?\nC: Listo, confirmo", Won, Service: "bikini"),
        new("won: customer names the day", "C: Hola, info de bikini\nA: $90.000, ¿te agendo el sábado?\nC: Sí, listo el sábado a las 3", Won, Service: "bikini"),
        new("won: booking and see you there", "C: Hola quiero separar una cita para mañana\nA: Con gusto, mañana a las 4 pm\nC: perfecto ahí estaré\nA: Quedas agendada, te esperamos", Won),
        new("won: full exchange", "C: Quiero cita para rostro\nA: ¿Te agendo mañana 3pm?\nC: Sí\nA: Listo, cita confirmada para mañana a las 3pm\nC: Gracias, ahí estaré", Won, Service: "rostro"),
        new("won: moved to another day", "C: Quiero agendar axilas\nA: Quedó agendada el sábado a las 10, te esperamos\nC: Puedo reprogramar la cita para el domingo?", Won, Service: "axilas"),

        // ---- Lost: refusals ---------------------------------------------------------------------------------------------------
        new("lost: no thanks", "C: cuánto cuesta axilas\nA: 60 mil\nC: no gracias", Lost, Loss: LossReason.NotInterested, Service: "axilas"),
        new("lost: no thanks, with a comma", "C: cuánto cuesta axilas\nA: 60 mil\nC: No, gracias", Lost, Loss: LossReason.NotInterested, Service: "axilas"),
        new("lost: thanks but no", "C: Muchas gracias pero no", Lost, Loss: LossReason.NotInterested),
        new("lost: not interested", "C: No me interesa", Lost, Loss: LossReason.NotInterested),
        new("lost: no longer interested", "C: Ya no me interesa, gracias", Lost, Loss: LossReason.NotInterested),
        new("lost: no longer want it", "C: Ya no quiero hacerlo, gracias", Lost, Loss: LossReason.NotInterested),
        new("lost: bare ya no after a question", "A: ¿Sigues interesada en la promo?\nC: Ya no", Lost, Loss: LossReason.NotInterested),
        new("lost: bare no quiero", "C: No quiero", Lost, Loss: LossReason.NotInterested),
        new("lost: too expensive", "C: Cuanto vale el rostro\nA: 80 mil\nC: Uy muy caro, no gracias", Lost, ObjectionType.Price, LossReason.Price, Service: "rostro"),
        new("lost: price raised earlier", "C: Es muy caro\nA: Tenemos plan de pagos\nC: No me interesa", Lost, ObjectionType.Price, LossReason.Price),
        new("lost: no time", "C: Mejor no, lo pensé y no tengo tiempo", Lost, ObjectionType.Timing, LossReason.Timing),
        new("lost: too far", "C: Me queda muy lejos, mejor no", Lost, ObjectionType.Location, LossReason.Location),
        new("lost: fear of pain", "C: Me da miedo que duela, mejor no", Lost, ObjectionType.Pain, LossReason.Other),
        new("lost: already booked elsewhere", "C: Ya agendé en otro lado, gracias", Lost, Loss: LossReason.Competitor),
        new("lost: cheaper elsewhere", "C: En otra clínica me cobran menos, no gracias", Lost, ObjectionType.Price, LossReason.Competitor),
        new("lost: cancels a booking", "C: Quiero agendar cita para axilas\nA: Quedó agendada para el sábado 10 am, te esperamos\nC: Hola, necesito cancelar la cita", Lost, Loss: LossReason.Other, Service: "axilas"),
        new("lost: refusal undone by a request to book", "C: No gracias\nA: Entiendo. ¿Y si te cuento de la promo 2x1?\nC: Ah, ¿desde cuándo está la promo? Quiero agendar", Follow, Reason: "precio_promocion"),
    ];

    private static readonly Dictionary<string, Case> ByName = Cases.ToDictionary(c => c.Name);

    public static TheoryData<string> CaseNames => new(Cases.Select(c => c.Name));

    [Fact]
    public void TheGoldenSet_HasAtLeastSixtyCases_WithUniqueNames()
    {
        Assert.True(Cases.Length >= 60, $"only {Cases.Length} cases");
        Assert.Equal(Cases.Length, ByName.Count);
    }

    [Fact]
    public void TheGoldenSet_CoversEveryOutcomeObjectionAndLossReasonTheRulesCanProduce()
    {
        Assert.Equal(
            new[] { Undetermined, Follow, Won, Lost },
            Cases.Select(c => c.Outcome).Distinct().OrderBy(o => o).ToArray());
        Assert.Equal(
            new[] { ObjectionType.None, ObjectionType.Price, ObjectionType.Timing, ObjectionType.Location, ObjectionType.Trust, ObjectionType.Pain, ObjectionType.Other },
            Cases.Select(c => c.Objection).Distinct().OrderBy(o => o).ToArray());
        Assert.Equal(
            new LossReason?[] { LossReason.Price, LossReason.Location, LossReason.Timing, LossReason.Competitor, LossReason.NotInterested, LossReason.Other },
            Cases.Where(c => c.Loss is not null).Select(c => c.Loss).Distinct().OrderBy(o => o).ToArray());
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ReadsTheChatAsExpected(string name)
    {
        var expected = ByName[name];

        var result = Classifier.Classify(ChatScript.Snapshot(expected.Chat));

        Assert.Equal(
            (expected.Outcome, expected.Objection, expected.Loss, expected.Service, expected.Reason),
            (result.Outcome, result.Objection, result.LossReason, result.ServiceInterest, result.InterestReason));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryResultIsMadeOfCodesOnly_AndIsRepeatable(string name)
    {
        var chat = ChatScript.Snapshot(ByName[name].Chat);

        var first = Classifier.Classify(chat);
        var second = Classifier.Classify(chat);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.RuleCodes, second.RuleCodes);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.InRange(first.Confidence, 0m, 1m);
        Assert.NotEmpty(first.RuleCodes);
        Assert.All(first.RuleCodes, code => Assert.Matches("^[a-z0-9_.]{1,60}$", code));
        Assert.Equal(first.RuleCodes.Count, first.RuleCodes.Distinct().Count());
        if (first.ServiceInterest is { } service)
        {
            Assert.Matches("^[a-z0-9_,]{1,100}$", service);
        }

        if (first.InterestReason is { } reason)
        {
            Assert.Matches("^[a-z0-9_]{1,200}$", reason);
        }

        Assert.Equal(first.Outcome == ClassificationOutcome.Lost, first.LossReason is not null);
    }

    private static readonly string[] CodePrefixes =
        ["service.", "reason.", "objection.", "outcome.", "loss.", "intent.", "ack.", "proposal.", "competitor", "inquiry"];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void NothingTheCustomerWroteIsCopiedIntoTheResult(string name)
    {
        // A word that exists nowhere in the dictionaries, added to every customer message: if the result held customer text, it would show.
        const string Marker = "zzqxsecreto";
        var plain = ChatScript.Snapshot(ByName[name].Chat);
        var marked = plain with
        {
            Messages = [.. plain.Messages.Select(m => m.Direction == MessageDirection.Inbound && m.Text is not null ? m with { Text = m.Text + ". " + Marker } : m)],
        };

        var result = Classifier.Classify(marked);

        var output = string.Join(' ', result.RuleCodes.Append(result.ServiceInterest).Append(result.InterestReason));
        Assert.DoesNotContain(Marker, output, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.RuleCodes, code => Assert.Contains(CodePrefixes, prefix => code.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Equal(Classifier.Classify(plain).Outcome, result.Outcome); // an unknown word changes nothing
    }

    [Fact]
    public void NoAmbiguousCaseOfTheSetEndsAsWon()
    {
        var wrong = Cases
            .Where(c => c.Outcome != Won)
            .Where(c => Classifier.Classify(ChatScript.Snapshot(c.Chat)).Outcome == Won)
            .Select(c => c.Name)
            .ToList();

        Assert.Empty(wrong);
    }
}
