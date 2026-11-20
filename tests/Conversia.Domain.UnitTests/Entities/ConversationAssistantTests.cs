using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>Every transition of the assistant's state machine (docs/ASSISTANT.md, "State machine").</summary>
public class ConversationAssistantTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    private static ConversationAssistant New() => ConversationAssistant.Create(Guid.NewGuid(), Guid.NewGuid(), isSandbox: false);

    private static ConversationAssistant Armed()
    {
        var a = New();
        a.CustomerWrote(T0, Delay);
        return a;
    }

    [Fact]
    public void A_new_row_is_off_with_no_timer()
    {
        var a = New();
        Assert.Equal(AssistantState.Off, a.State);
        Assert.Null(a.DueAt);
        Assert.False(a.IsDue(T0.AddDays(1)));
    }

    [Fact]
    public void Create_requires_the_company_and_the_conversation()
    {
        Assert.Throws<DomainException>(() => ConversationAssistant.Create(Guid.Empty, Guid.NewGuid(), false));
        Assert.Throws<DomainException>(() => ConversationAssistant.Create(Guid.NewGuid(), Guid.Empty, false));
    }

    [Fact]
    public void A_customer_message_arms_the_timer_with_the_delay()
    {
        var a = New();
        Assert.True(a.CustomerWrote(T0, Delay));
        Assert.Equal(AssistantState.Waiting, a.State);
        Assert.Equal(T0 + Delay, a.DueAt);
        Assert.Equal(T0, a.ArmedAt);
        Assert.False(a.IsDue(T0 + Delay - TimeSpan.FromSeconds(1)));
        Assert.True(a.IsDue(T0 + Delay));
    }

    [Fact]
    public void A_second_customer_message_keeps_the_due_time_but_moves_the_generation()
    {
        var a = Armed();
        var generation = a.Generation;
        Assert.True(a.CustomerWrote(T0.AddSeconds(30), Delay));
        Assert.Equal(T0 + Delay, a.DueAt);
        Assert.NotEqual(generation, a.Generation);
        Assert.False(a.IsCurrent(generation));
    }

    [Fact]
    public void A_customer_message_after_an_assistant_reply_arms_again()
    {
        var a = Armed();
        Assert.True(a.TryClaim(T0 + Delay, Lease));
        a.RecordReply(T0 + Delay, disclosed: false);
        Assert.Equal(AssistantState.Active, a.State);
        Assert.True(a.CustomerWrote(T0.AddMinutes(5), Delay));
        Assert.Equal(AssistantState.Waiting, a.State);
        Assert.Equal(1, a.AssistantTurns);
    }

    [Fact]
    public void A_customer_message_never_arms_a_conversation_pending_review()
    {
        var a = Armed();
        a.HandOff(AssistantHandoffReasons.Keyword, T0);
        Assert.False(a.CustomerWrote(T0.AddMinutes(1), Delay));
        Assert.Equal(AssistantState.HandedOff, a.State);
        Assert.Null(a.DueAt);
    }

    [Fact]
    public void A_person_writing_cancels_the_timer_and_holds_for_the_cooldown()
    {
        var a = Armed();
        var generation = a.Generation;
        a.HumanWrote(T0.AddSeconds(20), Cooldown);
        Assert.Equal(AssistantState.HumanTookOver, a.State);
        Assert.Null(a.DueAt);
        Assert.Null(a.ClaimedUntil);
        Assert.Equal(T0.AddSeconds(20) + Cooldown, a.HumanHoldUntil);
        Assert.False(a.IsCurrent(generation));
        Assert.False(a.CustomerWrote(T0.AddMinutes(10), Delay));
        Assert.Equal(AssistantState.HumanTookOver, a.State);
    }

    [Fact]
    public void After_the_cooldown_a_customer_message_arms_again_with_fresh_counters()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.RecordReply(T0 + Delay, disclosed: false);
        a.HumanWrote(T0.AddMinutes(2), Cooldown);
        Assert.True(a.CustomerWrote(T0.AddMinutes(2) + Cooldown, Delay));
        Assert.Equal(AssistantState.Waiting, a.State);
        Assert.Equal(0, a.AssistantTurns);
        Assert.Null(a.HumanHoldUntil);
    }

    [Fact]
    public void A_person_writing_takes_over_a_handed_off_conversation_too()
    {
        var a = Armed();
        a.HandOff(AssistantHandoffReasons.HumanRequested, T0);
        a.HumanWrote(T0.AddMinutes(1), Cooldown);
        Assert.Equal(AssistantState.HumanTookOver, a.State);
        Assert.Null(a.HandoffReason);
    }

    [Fact]
    public void An_explicit_take_over_holds_until_handed_back()
    {
        var a = Armed();
        var userId = Guid.NewGuid();
        a.TakeOver(userId, T0);
        Assert.Equal(AssistantState.HumanTookOver, a.State);
        Assert.Null(a.HumanHoldUntil);
        Assert.Equal(userId, a.TakenOverBy);
        Assert.Null(a.DueAt);
        Assert.False(a.CustomerWrote(T0.AddDays(3), Delay));
    }

    [Fact]
    public void A_take_over_needs_the_user()
    {
        Assert.Throws<DomainException>(() => Armed().TakeOver(Guid.Empty, T0));
    }

    [Fact]
    public void A_person_writing_after_an_explicit_take_over_keeps_it_indefinite()
    {
        var a = Armed();
        a.TakeOver(Guid.NewGuid(), T0);
        a.HumanWrote(T0.AddMinutes(1), Cooldown);
        Assert.Null(a.HumanHoldUntil);
    }

    [Fact]
    public void Handing_back_with_a_waiting_customer_arms_at_once_and_resets_the_counters()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.RecordReply(T0 + Delay, disclosed: true);
        a.RecordMisunderstanding();
        a.HandOff(AssistantHandoffReasons.Misunderstanding, T0.AddMinutes(3));
        var generation = a.Generation;
        a.HandBack(T0.AddMinutes(5), Delay);
        Assert.Equal(AssistantState.Waiting, a.State);
        Assert.Equal(T0.AddMinutes(5) + Delay, a.DueAt);
        Assert.Equal(0, a.AssistantTurns);
        Assert.Equal(0, a.MisunderstandingCount);
        Assert.Null(a.HandoffReason);
        Assert.Null(a.TakenOverBy);
        Assert.NotEqual(generation, a.Generation);
    }

    [Fact]
    public void Handing_back_without_a_waiting_customer_rests()
    {
        var a = Armed();
        a.TakeOver(Guid.NewGuid(), T0);
        a.HandBack(T0.AddMinutes(1), armAfter: null);
        Assert.Equal(AssistantState.Off, a.State);
        Assert.Null(a.DueAt);
        Assert.Null(a.HumanHoldUntil);
        Assert.True(a.CustomerWrote(T0.AddMinutes(2), Delay));
    }

    [Fact]
    public void A_claim_holds_the_timer_until_its_lease_ends()
    {
        var a = Armed();
        Assert.False(a.TryClaim(T0, Lease)); // not due yet
        Assert.True(a.TryClaim(T0 + Delay, Lease));
        Assert.True(a.IsGenerating(T0 + Delay));
        Assert.False(a.TryClaim(T0 + Delay + TimeSpan.FromSeconds(5), Lease)); // another instance
        Assert.False(a.IsDue(T0 + Delay + TimeSpan.FromSeconds(5)));
        Assert.True(a.IsDue(T0 + Delay + Lease)); // the holder crashed
        Assert.True(a.TryClaim(T0 + Delay + Lease, Lease));
    }

    [Fact]
    public void A_claim_is_current_only_for_its_generation_while_waiting()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        var generation = a.Generation;
        Assert.True(a.IsCurrent(generation));
        Assert.False(a.IsCurrent(generation + 1));
        a.RecordReply(T0 + Delay, disclosed: false);
        Assert.False(a.IsCurrent(generation));
    }

    [Fact]
    public void A_reply_counts_a_turn_and_clears_the_timer()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.RecordReply(T0 + Delay, disclosed: true);
        Assert.Equal(AssistantState.Active, a.State);
        Assert.Equal(1, a.AssistantTurns);
        Assert.Null(a.DueAt);
        Assert.Null(a.ClaimedUntil);
        Assert.Null(a.ArmedAt);
        Assert.Equal(T0 + Delay, a.LastRepliedAt);
        Assert.Equal(T0 + Delay, a.DisclosedAt);
    }

    [Fact]
    public void The_first_disclosure_is_kept()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.RecordReply(T0 + Delay, disclosed: true);
        a.CustomerWrote(T0.AddMinutes(3), Delay);
        a.TryClaim(T0.AddMinutes(4), Lease);
        a.RecordReply(T0.AddMinutes(4), disclosed: true);
        Assert.Equal(T0 + Delay, a.DisclosedAt);
        Assert.Equal(2, a.AssistantTurns);
    }

    [Fact]
    public void A_reply_needs_a_waiting_timer()
    {
        Assert.Throws<DomainException>(() => New().RecordReply(T0, disclosed: false));
    }

    [Fact]
    public void A_handoff_records_its_reason_and_moves_the_generation()
    {
        var a = Armed();
        var generation = a.Generation;
        a.HandOff(AssistantHandoffReasons.MaxTurns, T0.AddMinutes(1));
        Assert.Equal(AssistantState.HandedOff, a.State);
        Assert.Equal(AssistantHandoffReasons.MaxTurns, a.HandoffReason);
        Assert.Equal(T0.AddMinutes(1), a.HandedOffAt);
        Assert.Null(a.DueAt);
        Assert.Null(a.ClaimedUntil);
        Assert.NotEqual(generation, a.Generation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Max Turns")]
    [InlineData("motivo-libre")]
    [InlineData("a_reason_code_that_is_far_too_long_for_the_column")]
    public void A_handoff_reason_is_a_code(string reason)
    {
        Assert.Throws<DomainException>(() => Armed().HandOff(reason, T0));
    }

    [Fact]
    public void Every_known_reason_is_a_valid_code()
    {
        foreach (var reason in AssistantHandoffReasons.All)
        {
            Armed().HandOff(reason, T0);
        }
    }

    [Fact]
    public void A_discard_rests_off_before_any_reply_and_active_after_one()
    {
        var first = Armed();
        first.TryClaim(T0 + Delay, Lease);
        first.Discard();
        Assert.Equal(AssistantState.Off, first.State);
        Assert.Null(first.DueAt);
        Assert.Null(first.ClaimedUntil);

        var later = Armed();
        later.TryClaim(T0 + Delay, Lease);
        later.RecordReply(T0 + Delay, false);
        later.CustomerWrote(T0.AddMinutes(3), Delay);
        later.TryClaim(T0.AddMinutes(4), Lease);
        later.Discard();
        Assert.Equal(AssistantState.Active, later.State);
    }

    [Fact]
    public void Releasing_a_claim_keeps_the_timer_due()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.ReleaseClaim();
        Assert.Equal(AssistantState.Waiting, a.State);
        Assert.Null(a.ClaimedUntil);
        Assert.True(a.IsDue(T0 + Delay + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_misunderstanding_is_counted()
    {
        var a = Armed();
        a.RecordMisunderstanding();
        a.RecordMisunderstanding();
        Assert.Equal(2, a.MisunderstandingCount);
    }

    [Fact]
    public void Redirects_AreCounted_InARow_AndAnOnTopicReply_OrANewEpisode_StartsAgain()
    {
        var a = Armed();
        a.RecordTopic(redirect: true);
        a.RecordTopic(redirect: true);
        Assert.Equal(2, a.OffTopicCount);
        a.RecordTopic(redirect: false);
        Assert.Equal(0, a.OffTopicCount);

        a.RecordTopic(redirect: true);
        a.HumanWrote(T0 + Delay, TimeSpan.Zero);
        a.CustomerWrote(T0 + Delay + TimeSpan.FromSeconds(1), Delay); // the hold is over: a new episode
        Assert.Equal(0, a.OffTopicCount);
    }

    [Fact]
    public void Misunderstandings_AreConsecutive_AGoodReplyClearsThem()
    {
        var a = Armed();
        a.RecordMisunderstanding();
        a.RecordMisunderstanding();
        a.ClearMisunderstandings();
        Assert.Equal(0, a.MisunderstandingCount);
    }

    [Fact]
    public void TheModelsHandoffCodes_AreKnown()
    {
        foreach (var code in new[] { "off_topic", "complaint", "invalid_reply", "sale_closed", "model_refused" })
        {
            Assert.Contains(code, AssistantHandoffReasons.All);
            var a = Armed();
            a.HandOff(code, T0);
            Assert.Equal(code, a.HandoffReason);
        }
    }

    // The race of docs/ASSISTANT.md: the job claimed the timer and calls the generator outside the lock; a person answers meanwhile.
    [Fact]
    public void A_person_answering_while_the_generator_runs_makes_the_claim_stale()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        var claimed = a.Generation;
        a.HumanWrote(T0 + Delay + TimeSpan.FromSeconds(3), Cooldown); // ingestion or the send path, under the conversation lock
        Assert.False(a.IsCurrent(claimed)); // the job re-checks under the lock before sending and discards its answer
        Assert.Throws<DomainException>(() => a.RecordReply(T0 + Delay + TimeSpan.FromSeconds(5), false));
    }

    [Fact]
    public void A_customer_writing_while_the_generator_runs_makes_the_claim_stale_but_keeps_the_timer()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        var claimed = a.Generation;
        a.CustomerWrote(T0 + Delay + TimeSpan.FromSeconds(2), Delay);
        Assert.False(a.IsCurrent(claimed));
        Assert.Equal(AssistantState.Waiting, a.State);
        a.ReleaseClaim();
        Assert.True(a.IsDue(T0 + Delay + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Times_must_be_utc()
    {
        Assert.Throws<DomainException>(() => New().CustomerWrote(DateTime.SpecifyKind(T0, DateTimeKind.Unspecified), Delay));
    }
}

/// <summary>Review round of unit 5a: claims move the generation, pending sends, own echoes, hand-back guard, hastening.</summary>
public class ConversationAssistantReviewTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static ConversationAssistant Armed()
    {
        var a = ConversationAssistant.Create(Guid.NewGuid(), Guid.NewGuid(), false);
        a.CustomerWrote(T0, Delay);
        return a;
    }

    [Fact]
    public void Each_claim_moves_the_generation_and_counts_an_attempt_so_a_holder_whose_lease_expired_is_stale()
    {
        var a = Armed();
        Assert.True(a.TryClaim(T0 + Delay, Lease));
        var first = a.Generation;
        Assert.Equal(1, a.Attempts);
        Assert.True(a.TryClaim(T0 + Delay + Lease, Lease));
        Assert.Equal(2, a.Attempts);
        Assert.False(a.IsCurrent(first));
        Assert.True(a.IsCurrent(a.Generation));
    }

    [Fact]
    public void A_new_arming_starts_the_attempts_again()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.RecordReply(T0 + Delay, false);
        a.CustomerWrote(T0.AddMinutes(5), Delay);
        Assert.Equal(0, a.Attempts);
    }

    [Fact]
    public void A_pending_send_recognizes_its_own_echo_by_text_within_the_window_only()
    {
        var a = Armed();
        a.TryClaim(T0 + Delay, Lease);
        a.BeginSend(T0 + Delay, "hash-1");
        Assert.True(a.IsSendPending);
        Assert.True(a.IsOwnEcho("hash-1", T0 + Delay + TimeSpan.FromSeconds(5), Lease));
        Assert.False(a.IsOwnEcho("hash-2", T0 + Delay + TimeSpan.FromSeconds(5), Lease));
        Assert.False(a.IsOwnEcho("hash-1", T0 + Delay + Lease + TimeSpan.FromSeconds(1), Lease));
        a.RecordReply(T0 + Delay, false);
        Assert.False(a.IsSendPending);
        Assert.False(a.IsOwnEcho("hash-1", T0 + Delay, Lease));
    }

    [Fact]
    public void A_send_can_only_begin_on_a_waiting_timer_and_a_handoff_clears_it()
    {
        Assert.Throws<DomainException>(() => ConversationAssistant.Create(Guid.NewGuid(), Guid.NewGuid(), false).BeginSend(T0, "h"));
        var a = Armed();
        a.BeginSend(T0, "h");
        a.HandOff(AssistantHandoffReasons.SendUnknown, T0);
        Assert.False(a.IsSendPending);
    }

    [Theory]
    [InlineData(AssistantState.Off)]
    [InlineData(AssistantState.Waiting)]
    [InlineData(AssistantState.Active)]
    public void Hand_back_does_nothing_unless_a_person_holds_it_or_it_is_pending_review(AssistantState state)
    {
        var a = ConversationAssistant.Create(Guid.NewGuid(), Guid.NewGuid(), false);
        if (state != AssistantState.Off)
        {
            a.CustomerWrote(T0, Delay);
        }

        if (state == AssistantState.Active)
        {
            a.TryClaim(T0 + Delay, Lease);
            a.RecordReply(T0 + Delay, false);
        }

        a.RecordMisunderstanding();
        var generation = a.Generation;
        Assert.False(a.HandBack(T0.AddMinutes(10), Delay));
        Assert.Equal(state, a.State);
        Assert.Equal(1, a.MisunderstandingCount);
        Assert.Equal(generation, a.Generation);
    }

    [Fact]
    public void Hand_back_works_from_a_hold_and_from_pending_review()
    {
        var held = Armed();
        held.TakeOver(Guid.NewGuid(), T0);
        Assert.True(held.HandBack(T0, null));
        var pending = Armed();
        pending.HandOff(AssistantHandoffReasons.Keyword, T0);
        Assert.True(pending.HandBack(T0, Delay));
        Assert.Equal(AssistantState.Waiting, pending.State);
    }

    [Fact]
    public void A_request_for_a_person_hastens_the_timer_never_delays_it()
    {
        var a = Armed();
        a.Hasten(T0.AddSeconds(2), TimeSpan.FromSeconds(5));
        Assert.Equal(T0.AddSeconds(7), a.DueAt);
        a.Hasten(T0.AddSeconds(3), TimeSpan.FromSeconds(30));
        Assert.Equal(T0.AddSeconds(7), a.DueAt);
        var off = ConversationAssistant.Create(Guid.NewGuid(), Guid.NewGuid(), false);
        off.Hasten(T0, TimeSpan.FromSeconds(5));
        Assert.Null(off.DueAt);
    }
}
