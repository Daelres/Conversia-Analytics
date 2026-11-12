using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.TestSupport;

namespace Conversia.Application.UnitTests.Billing;

public class DunningPolicyTests
{
    private static readonly DateOnly Due = new(2026, 12, 15);

    [Theory]
    [InlineData(0, "2026-12-15")] // the first attempt is on the due date
    [InlineData(1, "2026-12-16")] // then 1, 3 and 5 days after it
    [InlineData(2, "2026-12-18")]
    [InlineData(3, "2026-12-20")]
    [InlineData(4, null)] // none left
    public void Automatic_attempts_follow_the_default_retry_days(int started, string? expected)
        => Assert.Equal(expected is null ? null : DateOnly.Parse(expected), DunningPolicy.NextAttemptDate(Due, started, BillingSettings.Default));

    [Fact]
    public void Configured_retry_days_replace_the_defaults()
    {
        var settings = new BillingSettings(retryDays: [2]);

        Assert.Equal(Due.AddDays(2), DunningPolicy.NextAttemptDate(Due, 1, settings));
        Assert.Null(DunningPolicy.NextAttemptDate(Due, 2, settings));
        Assert.Null(DunningPolicy.NextAttemptDate(Due, 1, new BillingSettings(retryDays: [])));
    }

    [Fact]
    public void The_company_is_suspended_after_the_grace_period_and_cancelled_after_the_suspension_window()
    {
        Assert.Equal(new DateOnly(2026, 12, 22), DunningPolicy.SuspensionDate(Due, BillingSettings.Default));
        Assert.Equal(new DateOnly(2027, 1, 21), DunningPolicy.CancellationDate(new DateOnly(2026, 12, 22), BillingSettings.Default));
        Assert.Null(DunningPolicy.CancellationDate(new DateOnly(2026, 12, 22), new BillingSettings(cancelAfterSuspendedDays: 0)));
        Assert.Equal(new DateOnly(2026, 12, 18), DunningPolicy.SuspensionDate(Due, new BillingSettings(graceDays: 3)));
    }

    [Theory]
    [InlineData(new[] { 3, 1 })]
    [InlineData(new[] { 1, 1 })]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 91 })]
    public void Retry_days_must_be_increasing_and_within_range(int[] days)
        => Assert.Throws<ArgumentException>(() => new BillingSettings(retryDays: days));

    [Theory]
    [InlineData(SubscriptionStatus.Active, false, false, DunningStage.None)]
    [InlineData(SubscriptionStatus.Trialing, false, false, DunningStage.None)]
    [InlineData(SubscriptionStatus.PastDue, true, false, DunningStage.Retrying)]
    [InlineData(SubscriptionStatus.PastDue, true, true, DunningStage.Suspended)]
    [InlineData(SubscriptionStatus.Active, true, false, DunningStage.Retrying)]
    [InlineData(SubscriptionStatus.Cancelled, false, false, DunningStage.Cancelled)]
    [InlineData(SubscriptionStatus.Cancelled, true, true, DunningStage.Cancelled)]
    public void The_stage_comes_from_the_status_and_the_overdue_charge(SubscriptionStatus status, bool overdue, bool suspended, DunningStage expected)
        => Assert.Equal(expected, DunningPolicy.Stage(status, overdue, suspended));

    [Fact]
    public void Every_billing_problem_has_a_spanish_message_a_code_and_a_status()
    {
        foreach (var problem in Enum.GetValues<BillingProblem>())
        {
            var exception = new BillingException(problem);
            Assert.True(EnglishLeaks.Find(exception.Message).Count == 0, exception.Message);
            Assert.True(EnglishLeaks.LooksSpanish(exception.Message), exception.Message);
            Assert.NotEqual("billing_problem", exception.Code);
            Assert.InRange(exception.StatusCode, 400, 599);
        }

        Assert.Equal(Enum.GetValues<BillingProblem>().Length, Enum.GetValues<BillingProblem>().Select(BillingException.CodeOf).Distinct().Count());
    }
}
