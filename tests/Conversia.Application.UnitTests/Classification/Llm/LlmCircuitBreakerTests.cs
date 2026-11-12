using Conversia.Application.Classification.Llm;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>The process-wide breaker of the LLM path (design D21, spec CC-6): transport failures only, a cooldown, then one probe at a time.</summary>
public class LlmCircuitBreakerTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(300);

    private static LlmCircuitBreaker New(int failures = 5) => new(failures, Cooldown);

    private static void FailTimes(LlmCircuitBreaker breaker, int times, DateTime at)
    {
        for (var i = 0; i < times; i++)
        {
            var ticket = breaker.TryEnter(at);
            Assert.True(ticket.Admitted);
            breaker.RecordTransportFailure(ticket, at);
        }
    }

    [Fact]
    public void AClosedBreaker_AdmitsEveryCall()
    {
        var breaker = New();

        var ticket = breaker.TryEnter(T0);

        Assert.Equal((true, false, BreakerDenial.None), (ticket.Admitted, ticket.IsProbe, ticket.Denial));
        Assert.Equal(BreakerState.Closed, breaker.State(T0));
    }

    [Fact]
    public void ItOpens_AtTheConfiguredNumberOfConsecutiveTransportFailures_AndDeniesUntilTheCooldownEnds()
    {
        var breaker = New(failures: 5);

        FailTimes(breaker, 4, T0);
        Assert.Equal(BreakerState.Closed, breaker.State(T0));

        FailTimes(breaker, 1, T0.AddSeconds(10));
        var denied = breaker.TryEnter(T0.AddSeconds(11));

        Assert.Equal(BreakerState.Open, breaker.State(T0.AddSeconds(11)));
        Assert.Equal((false, BreakerDenial.Open, (DateTime?)T0.AddSeconds(10) + Cooldown), (denied.Admitted, denied.Denial, denied.ReopensAt));
    }

    [Fact]
    public void ASuccess_ResetsTheConsecutiveCount()
    {
        var breaker = New(failures: 3);
        FailTimes(breaker, 2, T0);

        breaker.RecordSuccess(breaker.TryEnter(T0));
        FailTimes(breaker, 2, T0);

        Assert.Equal(BreakerState.Closed, breaker.State(T0));
    }

    [Fact]
    public void AfterTheCooldown_ExactlyOneProbeIsAdmitted_AndTheOthersAreDeferred()
    {
        // CC-6 S12: three conversations due at the end of the cooldown, one probe call.
        var breaker = New(failures: 1);
        FailTimes(breaker, 1, T0);
        var reopen = T0 + Cooldown;

        var probe = breaker.TryEnter(reopen);
        var second = breaker.TryEnter(reopen);
        var third = breaker.TryEnter(reopen.AddSeconds(1));

        Assert.Equal((true, true), (probe.Admitted, probe.IsProbe));
        Assert.Equal((false, BreakerDenial.ProbeInFlight), (second.Admitted, second.Denial));
        Assert.Equal((false, BreakerDenial.ProbeInFlight), (third.Admitted, third.Denial));
        Assert.Equal(BreakerState.HalfOpen, breaker.State(reopen));
    }

    [Fact]
    public void ASuccessfulProbe_ClosesTheBreaker()
    {
        var breaker = New(failures: 1);
        FailTimes(breaker, 1, T0);
        var probe = breaker.TryEnter(T0 + Cooldown);

        breaker.RecordSuccess(probe);

        Assert.Equal(BreakerState.Closed, breaker.State(T0 + Cooldown));
        Assert.True(breaker.TryEnter(T0 + Cooldown).Admitted);
    }

    [Fact]
    public void AFailedProbe_OpensItForAnotherCooldown()
    {
        var breaker = New(failures: 5);
        FailTimes(breaker, 5, T0);
        var reopen = T0 + Cooldown;
        var probe = breaker.TryEnter(reopen);

        breaker.RecordTransportFailure(probe, reopen.AddSeconds(2));

        var denied = breaker.TryEnter(reopen.AddSeconds(3));
        Assert.Equal((BreakerDenial.Open, (DateTime?)reopen.AddSeconds(2) + Cooldown), (denied.Denial, denied.ReopensAt));
    }

    [Fact]
    public void AnAbandonedProbe_FreesTheProbeSlot_WithoutChangingTheState()
    {
        // The probe ended in an unexpected exception (neither an answer nor a transport failure): the next due conversation may probe.
        var breaker = New(failures: 1);
        FailTimes(breaker, 1, T0);
        var probe = breaker.TryEnter(T0 + Cooldown);

        breaker.Abandon(probe);

        var next = breaker.TryEnter(T0 + Cooldown);
        Assert.Equal((true, true), (next.Admitted, next.IsProbe));
    }

    [Fact]
    public void ADeniedTicket_ChangesNothingWhenRecorded()
    {
        var breaker = New(failures: 1);
        FailTimes(breaker, 1, T0);
        var denied = breaker.TryEnter(T0.AddSeconds(1));

        breaker.RecordSuccess(denied);

        Assert.Equal(BreakerState.Open, breaker.State(T0.AddSeconds(1)));
    }

    [Fact]
    public async Task ParallelCallersAfterTheCooldown_GetExactlyOneProbe()
    {
        var breaker = New(failures: 1);
        FailTimes(breaker, 1, T0);
        var due = T0 + Cooldown;
        using var start = new ManualResetEventSlim();

        var callers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return breaker.TryEnter(due);
        })).ToList();
        start.Set();
        var tickets = await Task.WhenAll(callers);

        Assert.Single(tickets, t => t.Admitted);
        Assert.True(tickets.Single(t => t.Admitted).IsProbe);
        Assert.Equal(63, tickets.Count(t => t.Denial == BreakerDenial.ProbeInFlight));
    }

    [Fact]
    public void AMisconfiguration_OpensTheBreakerAtOnce_AndLogsOneErrorPerWindow_NamingTheConditionOnly()
    {
        var log = new ListLogger();
        var breaker = new LlmCircuitBreaker(5, Cooldown, log);

        breaker.RecordMisconfiguration(breaker.TryEnter(T0), T0, "authentication");
        breaker.TryEnter(T0.AddSeconds(1));
        var reopen = T0 + Cooldown;
        breaker.RecordMisconfiguration(breaker.TryEnter(reopen), reopen, "authentication");

        Assert.Equal(BreakerState.Open, breaker.State(reopen));
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count); // one per open window, not one per call
        Assert.All(errors, e => Assert.Contains("authentication", e.Message));
    }

    [Fact]
    public void Transitions_AreLoggedAsWarnings_AndDenialsOncePerWindow()
    {
        var log = new ListLogger();
        var breaker = new LlmCircuitBreaker(2, Cooldown, log);

        FailTimes(breaker, 2, T0);
        for (var i = 0; i < 10; i++)
        {
            breaker.TryEnter(T0.AddSeconds(i + 1));
        }

        var probe = breaker.TryEnter(T0 + Cooldown);
        for (var i = 0; i < 10; i++)
        {
            breaker.TryEnter(T0 + Cooldown);
        }

        breaker.RecordSuccess(probe);

        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains("opened", warnings[0]);
        Assert.Contains("2", warnings[0]);
        Assert.Contains("300", warnings[0]);
        Assert.Contains("half-open", warnings[1]);
        Assert.Contains("closed", warnings[2]);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Information)); // one for the open window, one for the probe window
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(5, 0)]
    public void InvalidSettings_AreRejected(int failures, int cooldownSeconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LlmCircuitBreaker(failures, TimeSpan.FromSeconds(cooldownSeconds)));
}
