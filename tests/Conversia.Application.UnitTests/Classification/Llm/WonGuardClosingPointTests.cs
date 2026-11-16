using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>
/// The Won guard keeps its semantics (the rules have the last word on Won, both ways) and keeps the closing point coherent with the stage it
/// allows: a Won reached the booking and was closed by the business; a downgraded Won did not reach a confirmed booking.
/// </summary>
public class WonGuardClosingPointTests
{
    private static ClassificationResult Reading(ClassificationOutcome outcome, ClosingPointReading? point)
        => new(null, null, ObjectionType.None, outcome, null, 0.8m, ["llm.outcome"], ClosingPoint: point);

    private static readonly ClassificationResult RulesWon = Reading(ClassificationOutcome.Won, null);
    private static readonly ClassificationResult RulesFollow = Reading(ClassificationOutcome.InFollowUp, null);

    [Fact]
    public void ARulesWon_RaisesTheStageToTheBooking_AndTheAdvisorClosedIt()
    {
        var llm = Reading(ClassificationOutcome.InFollowUp, new ClosingPointReading(ReachedStage.PriceShared, OffChannelMove.None, null, ClosedBy.Unknown, false));

        var point = WonGuard.Apply(llm, RulesWon).Result.ClosingPoint!;

        Assert.Equal((ReachedStage.AppointmentBooked, ClosedBy.Advisor), (point.ReachedStage, point.ClosedBy));
    }

    [Fact]
    public void ARulesWon_WasClosedInTheChat_ByTheAdvisor_WhateverTheModelSaid()
    {
        var llm = Reading(ClassificationOutcome.Won, new ClosingPointReading(ReachedStage.AppointmentBooked, OffChannelMove.PhoneCall, 2, ClosedBy.CustomerSelfService, true));

        var point = WonGuard.Apply(llm, RulesWon).Result.ClosingPoint!;

        Assert.Equal((ClosedBy.Advisor, false, OffChannelMove.PhoneCall), (point.ClosedBy, point.ClosedOutsideSystem, point.MovedOffChannel));
    }

    [Fact]
    public void ALostReading_WasNotClosed_AndDidNotContinueOutside()
    {
        var llm = Reading(ClassificationOutcome.Lost, new ClosingPointReading(ReachedStage.PriceShared, OffChannelMove.PhoneCall, 2, ClosedBy.Advisor, true)) with { LossReason = LossReason.Price };

        var point = WonGuard.Apply(llm, Reading(ClassificationOutcome.Lost, null) with { LossReason = LossReason.Price }).Result.ClosingPoint!;

        Assert.Equal((ClosedBy.Unknown, false), (point.ClosedBy, point.ClosedOutsideSystem));
    }

    [Fact]
    public void ARulesWon_KeepsAPaymentStep()
    {
        var llm = Reading(ClassificationOutcome.Won, new ClosingPointReading(ReachedStage.PaymentOrDeposit, OffChannelMove.None, null, ClosedBy.Advisor, false));

        Assert.Equal(ReachedStage.PaymentOrDeposit, WonGuard.Apply(llm, RulesWon).Result.ClosingPoint!.ReachedStage);
    }

    [Fact]
    public void ADowngradedWon_DidNotReachTheBooking_NorWasClosedInTheChat()
    {
        var llm = Reading(ClassificationOutcome.Won, new ClosingPointReading(ReachedStage.AppointmentBooked, OffChannelMove.None, null, ClosedBy.Advisor, false));

        var decision = WonGuard.Apply(llm, RulesFollow);

        Assert.True(decision.WonDowngraded);
        Assert.Equal((ReachedStage.AppointmentProposed, ClosedBy.Unknown), (decision.Result.ClosingPoint!.ReachedStage, decision.Result.ClosingPoint.ClosedBy));
    }

    [Fact]
    public void ADowngradedWon_ThatContinuedOutside_KeepsWhoClosedIt()
    {
        // "te llamo y cerramos": the advisor closed it by phone, which the chat cannot confirm as a booking but the text shows.
        var llm = Reading(ClassificationOutcome.Won, new ClosingPointReading(ReachedStage.AppointmentProposed, OffChannelMove.PhoneCall, 3, ClosedBy.Advisor, true));

        Assert.Equal(ClosedBy.Advisor, WonGuard.Apply(llm, RulesFollow).Result.ClosingPoint!.ClosedBy);
    }

    [Fact]
    public void WithoutAClosingPoint_NothingIsInvented()
    {
        Assert.Null(WonGuard.Apply(Reading(ClassificationOutcome.InFollowUp, null), RulesWon).Result.ClosingPoint);
        Assert.Null(WonGuard.Apply(Reading(ClassificationOutcome.Won, null), RulesFollow).Result.ClosingPoint);
    }

    [Fact]
    public void AnAgreeingReading_IsUntouched()
    {
        var point = new ClosingPointReading(ReachedStage.ObjectionRaised, OffChannelMove.InPerson, 2, ClosedBy.Unknown, true);

        Assert.Equal(point, WonGuard.Apply(Reading(ClassificationOutcome.InFollowUp, point), RulesFollow).Result.ClosingPoint);
    }
}
