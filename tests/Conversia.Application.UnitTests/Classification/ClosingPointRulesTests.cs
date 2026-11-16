using Conversia.Application.Classification;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>
/// The rules fallback of the closing point (docs/CLASSIFICATION.md, "Closing point"): conservative, obvious cues only. When unsure it says
/// nothing (no stage, no move, unknown closer). It never changes what the classifier already read (outcome, objection, service...).
/// </summary>
public class ClosingPointRulesTests
{
    private static readonly RuleBasedConversationClassifier Classifier = RuleBasedConversationClassifier.CreateDefault();

    private static ClosingPointReading Read(string chat) => Classifier.Classify(ChatScript.Snapshot(chat)).ClosingPoint!;

    // ---- How far it got ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("C: Hola buenas tardes", ReachedStage.FirstContact)]
    [InlineData("C: [audio]", ReachedStage.FirstContact)]
    [InlineData("C: Cuánto vale la depilación de axilas?", ReachedStage.NeedsIdentified)]
    [InlineData("C: Me caso en diciembre y quiero verme bien", ReachedStage.NeedsIdentified)]
    [InlineData("C: cuánto vale rostro\nA: 80 mil por sesión", ReachedStage.PriceShared)]
    [InlineData("C: precio de axilas?\nA: La sesión está en $60.000", ReachedStage.PriceShared)]
    [InlineData("C: precio de bikini?\nA: Son 120.000 pesos la sesión", ReachedStage.PriceShared)]
    [InlineData("C: cuánto vale rostro\nA: 80 mil por sesión\nC: uy está muy caro", ReachedStage.ObjectionRaised)]
    [InlineData("C: Hola, quiero agendar una cita", ReachedStage.AppointmentProposed)]
    [InlineData("C: info de piernas\nA: ¿Te agendo una valoración gratis?", ReachedStage.AppointmentProposed)]
    [InlineData("C: quiero agendar axilas\nA: Listo, quedó agendada para el sábado a las 10", ReachedStage.AppointmentBooked)]
    [InlineData("C: quiero agendar axilas\nA: Listo, quedó agendada para el sábado\nC: necesito cancelar mi cita", ReachedStage.AppointmentBooked)]
    [InlineData("C: quiero agendar axilas\nA: Quedó agendada para el sábado, para separarla son 30 mil de abono\nC: ya hice la transferencia, te envío el comprobante", ReachedStage.PaymentOrDeposit)]
    [InlineData("C: quiero agendar\nA: Listo, recibimos tu abono", ReachedStage.PaymentOrDeposit)]
    public void ReadsTheFurthestStep(string chat, ReachedStage expected)
        => Assert.Equal(expected, Read(chat).ReachedStage);

    [Theory]
    [InlineData("A: Hola, te escribimos de Láser Spa")]
    [InlineData("A: La sesión de axilas está en 60 mil")]
    public void WithoutTheCustomer_NoStageIsRead(string chat)
        => Assert.Null(Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: hola\nA: mil gracias por escribirnos")] // "mil gracias" is courtesy, not an amount
    [InlineData("C: hola\nA: te esperamos a las 10")] // an hour is not a price
    [InlineData("C: hola, son 3 sesiones?\nA: sí, son 3 sesiones")]
    [InlineData("C: el precio es 80 mil?\nA: Hola, ¿qué zona te interesa?")] // the customer saying an amount is not the business sharing it
    public void APriceNeedsAnAmountWrittenByTheBusiness(string chat)
        => Assert.NotEqual(ReachedStage.PriceShared, Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: ¿ya pagaste el abono?")]
    [InlineData("C: no he hecho el abono todavía")]
    [InlineData("C: ya pagué el abono en otro lado, gracias")]
    [InlineData("A: ¿ya recibimos tu abono?\nC: hola")]
    public void PaymentNeedsAStatement_NotAQuestion_ANegation_OrAnotherProvider(string chat)
        => Assert.NotEqual(ReachedStage.PaymentOrDeposit, Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: hola\nA: Somos líderes, más de 10 mil pacientes atendidos")]
    [InlineData("C: hola\nA: Tenemos 3 millones de clientes felices")]
    public void AnAmountThatIsNotAPrice_IsNotAPrice(string chat)
        => Assert.NotEqual(ReachedStage.PriceShared, Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: precio de axilas?\nA: El valor es de 60 mil")]
    [InlineData("C: precio?\nA: Piernas 180 mil la sesión")]
    [InlineData("C: precio?\nA: Axilas: 60 mil")]
    [InlineData("C: cuánto vale piernas?\nA: 200 mil")] // a bare amount that answers the customer's question about the price
    public void APriceNeedsAPriceWordOrAZone_InItsClause(string chat)
        => Assert.Equal(ReachedStage.PriceShared, Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: te envío el comprobante")] // an intent: not paid yet
    [InlineData("C: le mando el comprobante en un rato")]
    [InlineData("C: quiero agendar\nA: si el pago queda confirmado hoy, te agendo el sábado")]
    [InlineData("C: quiero agendar\nA: cuando recibamos tu abono te confirmo")]
    public void APaymentThatIsOnlyAnIntentOrACondition_IsNotAPayment(string chat)
        => Assert.NotEqual(ReachedStage.PaymentOrDeposit, Read(chat).ReachedStage);

    [Theory]
    [InlineData("C: ya te envié el comprobante")]
    [InlineData("C: listo, aquí está el comprobante")]
    [InlineData("C: quiero agendar\nA: perfecto, ya recibimos tu abono")]
    public void ACompletedPayment_StillCounts(string chat)
        => Assert.Equal(ReachedStage.PaymentOrDeposit, Read(chat).ReachedStage);

    // ---- Moving off the chat -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("C: quiero info de axilas\nA: Te llamo en 5 minutos y cerramos", OffChannelMove.PhoneCall, 2)]
    [InlineData("C: mejor llámame, es más fácil", OffChannelMove.PhoneCall, 1)]
    [InlineData("C: precio de piernas\nA: 200 mil\nC: listo, paso por la sede y lo hablamos", OffChannelMove.InPerson, 3)]
    [InlineData("C: hola\nA: hola, acércate a la sede del centro y te explicamos", OffChannelMove.InPerson, 2)]
    [InlineData("C: escríbeme al otro número por favor", OffChannelMove.OtherChannel, 1)]
    [InlineData("C: hola\nA: te mando la info por correo", OffChannelMove.OtherChannel, 2)]
    [InlineData("C: te llamo luego\nC: mejor escríbeme por instagram", OffChannelMove.OtherChannel, 2)] // the latest move wins
    public void ReadsAMoveOffTheChat_AndWhereItShows(string chat, OffChannelMove move, int line)
    {
        var point = Read(chat);

        Assert.Equal((move, (int?)line), (point.MovedOffChannel, point.OffChannelEvidenceIndex));
    }

    [Theory]
    [InlineData("C: ¿me pueden llamar?")] // a question is not a move yet
    [InlineData("C: no me llamen, prefiero por aquí")]
    [InlineData("C: los vi por instagram, cuánto vale axilas?")] // where they saw the ad is the origin, not a move
    [InlineData("C: me llamo Ana, quiero info")]
    [InlineData("C: ¿atienden en persona?")]
    [InlineData("C: hola\nA: no te llamo, tranquila, seguimos por aquí")]
    [InlineData("C: te llamo cuando salga del trabajo")] // a deferral
    [InlineData("C: te llamo si me decido")] // a condition
    [InlineData("C: hola\nA: si quieres te llamo")]
    [InlineData("C: paso por la sede cuando pueda")]
    [InlineData("C: hola\nA: te mando el correo de la sede")] // the address, not the channel
    [InlineData("C: hola\nA: te paso el correo con la cotización")]
    public void NoMoveWithoutAnExplicitStatement(string chat)
    {
        var point = Read(chat);

        Assert.Equal((OffChannelMove.None, (int?)null), (point.MovedOffChannel, point.OffChannelEvidenceIndex));
    }

    // ---- Where it ended -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("C: precio de piernas\nA: 200 mil\nC: te llamo cuando salga del trabajo")]
    [InlineData("C: precio de piernas\nA: 200 mil\nC: te llamo si me decido")]
    public void ADeferredCall_DidNotContinueOutside(string chat)
    {
        var point = Read(chat);

        Assert.Equal((OffChannelMove.None, false), (point.MovedOffChannel, point.ClosedOutsideSystem));
    }

    [Theory]
    [InlineData("C: sí, te llamo ya mismo", OffChannelMove.PhoneCall)] // "sí," ends its clause: it is a yes, not a condition
    [InlineData("C: escríbeme a mi correo", OffChannelMove.OtherChannel)]
    [InlineData("C: hola\nA: seguimos por el Instagram de la sede", OffChannelMove.OtherChannel)]
    public void ADirectMove_StillCounts(string chat, OffChannelMove move)
        => Assert.Equal(move, Read(chat).MovedOffChannel);

    [Fact]
    public void AMoveThatIsTheLastCommercialStep_ContinuedOutsideTheSystem()
    {
        var point = Read("C: precio de piernas\nA: 200 mil\nA: te llamo y lo cerramos\nC: ok, gracias");

        Assert.True(point.ClosedOutsideSystem);
        Assert.Equal(ClosedBy.Unknown, point.ClosedBy); // who closed it on the phone cannot be told
    }

    [Theory]
    [InlineData("C: precio de piernas\nA: te llamo y lo cerramos\nC: espera, ¿y cuánto vale bikini?")] // the chat went on
    [InlineData("C: quiero agendar\nA: te llamo\nA: quedó agendada para el sábado a las 10")] // it closed in the chat
    [InlineData("C: precio de piernas\nA: 200 mil, te llamo\nC: no gracias, no me interesa")] // refused afterwards
    [InlineData("C: cuánto vale axilas?")]
    public void NotOutsideTheSystem_WhenTheChatWentOn_ClosedInIt_OrHadNoMove(string chat)
        => Assert.False(Read(chat).ClosedOutsideSystem);

    [Fact]
    public void ABookingTheBusinessConfirmed_WasClosedByTheAdvisor()
        => Assert.Equal(ClosedBy.Advisor, Read("C: quiero agendar axilas\nA: Quedó agendada para el sábado a las 10").ClosedBy);

    [Theory]
    [InlineData("C: hola, ya agendé por la página web, ¿qué debo llevar?")]
    [InlineData("C: listo, ya pagué por el link que me mandaron")]
    public void ASelfServiceBooking_WasClosedByTheCustomer_OutsideTheChat(string chat)
    {
        var point = Read(chat);

        Assert.Equal((ClosedBy.CustomerSelfService, true), (point.ClosedBy, point.ClosedOutsideSystem));
    }

    [Theory]
    [InlineData("C: ¿puedo agendar por la página?")]
    [InlineData("C: no agendé por la página, mejor por aquí")]
    [InlineData("C: cuánto vale axilas?\nA: 60 mil")]
    public void NoCloserIsGuessed(string chat)
        => Assert.Equal(ClosedBy.Unknown, Read(chat).ClosedBy);

    // ---- Who closed it and whether it went on outside: every combination (docs/CLASSIFICATION.md, "Closing point") ----------------

    private const string WonChat = "C: quiero agendar axilas\nA: Quedó agendada para el sábado a las 10";
    private const string LostEnd = "\nC: no gracias, no me interesa";
    private const string FollowChat = "C: precio de axilas?\nA: el valor es 60 mil";
    private const string SelfService = "\nC: ya pagué por el link";
    private const string MoveLast = "\nA: te llamo y lo cerramos\nC: ok, gracias";
    private const string MoveThenMore = "\nA: te llamo y lo cerramos\nC: espera, ¿y cuánto vale bikini?";

    public static TheoryData<string, string, ClassificationOutcome, ClosedBy, bool> Combinations => new()
    {
        // A booking confirmed in the chat: closed by the advisor, in the chat, whatever else was said.
        { "won", WonChat, ClassificationOutcome.Won, ClosedBy.Advisor, false },
        { "won + self-service", WonChat + SelfService, ClassificationOutcome.Won, ClosedBy.Advisor, false },
        { "won + move last", WonChat + MoveLast, ClassificationOutcome.Won, ClosedBy.Advisor, false },
        // Lost: nobody closed it, and it did not continue anywhere.
        { "lost + self-service", FollowChat + SelfService + LostEnd, ClassificationOutcome.Lost, ClosedBy.Unknown, false },
        { "lost + move", FollowChat + "\nA: te llamo y lo cerramos" + LostEnd, ClassificationOutcome.Lost, ClosedBy.Unknown, false },
        // Still open in the chat: a self-service booking or payment closed it outside, by the customer; a move that is the last step
        // continued outside, closer unknown.
        { "follow-up", FollowChat, ClassificationOutcome.InFollowUp, ClosedBy.Unknown, false },
        { "follow-up + self-service", FollowChat + SelfService, ClassificationOutcome.InFollowUp, ClosedBy.CustomerSelfService, true },
        { "follow-up + move last", FollowChat + MoveLast, ClassificationOutcome.InFollowUp, ClosedBy.Unknown, true },
        { "follow-up + move, then more", FollowChat + MoveThenMore, ClassificationOutcome.InFollowUp, ClosedBy.Unknown, false },
        { "follow-up + self-service + move last", FollowChat + SelfService + MoveLast, ClassificationOutcome.InFollowUp, ClosedBy.CustomerSelfService, true },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void TheCloser_AndWhetherItContinuedOutside_FollowTheOutcome(string name, string chat, ClassificationOutcome outcome, ClosedBy closedBy, bool outside)
    {
        var result = Classifier.Classify(ChatScript.Snapshot(chat));

        Assert.Equal((outcome, closedBy, outside), (result.Outcome, result.ClosingPoint!.ClosedBy, result.ClosingPoint.ClosedOutsideSystem));
        Assert.NotNull(name);
    }

    // ---- Nothing else changes -----------------------------------------------------------------------------------------

    [Fact]
    public void EveryReading_HasAClosingPoint_ThatIsCoherentWithTheOutcome()
    {
        foreach (var (_, chat, _) in ConversationClassifierGoldenSetTests.AllChats)
        {
            var result = Classifier.Classify(ChatScript.Snapshot(chat));
            var point = result.ClosingPoint;

            Assert.NotNull(point);
            Assert.Equal(point.MovedOffChannel == OffChannelMove.None, point.OffChannelEvidenceIndex is null);
            Assert.NotEqual(ClosedBy.AiAssistant, point.ClosedBy);
            if (result.Outcome == ClassificationOutcome.Won)
            {
                Assert.True(point.ReachedStage >= ReachedStage.AppointmentBooked, chat);
                Assert.Equal(ClosedBy.Advisor, point.ClosedBy);
            }
        }
    }
}
