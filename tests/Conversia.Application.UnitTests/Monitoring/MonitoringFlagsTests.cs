using Conversia.Application.Monitoring;

namespace Conversia.Application.UnitTests.Monitoring;

/// <summary>The threshold flags of the platform overview (docs/PLATFORM_ADMIN.md, "Monitoring"): pure, one flag per crossed threshold.</summary>
public class MonitoringFlagsTests
{
    private static readonly MonitoringThresholds Thresholds = MonitoringThresholds.Default;

    private static readonly MonitoringFlagInput Calm = new(
        HealthStatus: "Healthy", WebhookPending: 0, OldestPending: null, ClassificationWaiting: 0, OldestClassification: null, ClassificationStopped: 0,
        Deliveries24h: 1000, FailedDeliveries24h: 0, Errors24h: 0, Sends7d: 100, FailedSends7d: 0, SilentCompanies: 0, AiLimitedCompanies: 0,
        DeploymentCapReached: false, BreakerState: "Closed", LostUsageRecords: 0);

    private static IReadOnlyList<string> Codes(MonitoringFlagInput input) => MonitoringFlags.Evaluate(input, Thresholds).Select(f => f.Code).ToList();

    [Fact]
    public void ACalmPlatform_RaisesNoFlag() => Assert.Empty(Codes(Calm));

    [Fact]
    public void TheDefaults_AreDocumented()
        => Assert.Equal(
            (500, 10, 500, 60, 5, 20, 24, 20, 20, 10),
            (Thresholds.WebhookBacklog, Thresholds.WebhookOldestPendingMinutes, Thresholds.ClassificationBacklog, Thresholds.ClassificationOldestMinutes,
                Thresholds.FailedDeliveryRatePercent, Thresholds.MinDeliveriesForRate, Thresholds.SilentLineHours, Thresholds.ErrorsLast24h,
                Thresholds.SendFailureRatePercent, Thresholds.MinSendsForRate));

    [Theory]
    [InlineData("Degraded", "warning")]
    [InlineData("Unhealthy", "critical")]
    public void AHealthCheckThatIsNotHealthy_IsAFlag(string status, string severity)
    {
        var flag = Assert.Single(MonitoringFlags.Evaluate(Calm with { HealthStatus = status }, Thresholds));
        Assert.Equal(("health", severity), (flag.Code, flag.Severity));
    }

    [Fact]
    public void TheWebhookBacklog_IsFlaggedBySizeAndByAge_AtTheThreshold()
    {
        Assert.Empty(Codes(Calm with { WebhookPending = 499, OldestPending = TimeSpan.FromMinutes(9) }));
        Assert.Equal(["webhook_backlog", "webhook_backlog_age"], Codes(Calm with { WebhookPending = 500, OldestPending = TimeSpan.FromMinutes(10) }));
    }

    [Fact]
    public void TheClassificationBacklog_ItsAge_AndStoppedConversations_AreFlagged()
        => Assert.Equal(
            ["classification_backlog", "classification_backlog_age", "classification_stopped"],
            Codes(Calm with { ClassificationWaiting = 500, OldestClassification = TimeSpan.FromMinutes(60), ClassificationStopped = 1 }));

    [Fact]
    public void TheFailedDeliveryRate_NeedsEnoughDeliveriesToMeanSomething()
    {
        Assert.Empty(Codes(Calm with { Deliveries24h = 19, FailedDeliveries24h = 19 }));
        Assert.Empty(Codes(Calm with { Deliveries24h = 100, FailedDeliveries24h = 4 }));
        Assert.Equal(["failed_deliveries"], Codes(Calm with { Deliveries24h = 100, FailedDeliveries24h = 5 }));
    }

    [Fact]
    public void ErrorsSendFailuresSilentCompaniesAndTheAiLimits_AreFlagged()
        => Assert.Equal(
            ["errors", "send_failures", "silent_companies", "ai_limited_companies", "ai_deployment_cap", "llm_breaker_open"],
            Codes(Calm with
            {
                Errors24h = 20, Sends7d = 10, FailedSends7d = 2, SilentCompanies = 1, AiLimitedCompanies = 2, DeploymentCapReached = true, BreakerState = "Open",
            }));

    [Fact]
    public void EveryFlag_HasASpanishMessageWithTheNumbers()
    {
        var flags = MonitoringFlags.Evaluate(Calm with { WebhookPending = 612, SilentCompanies = 3 }, Thresholds);

        Assert.Contains("612", flags.Single(f => f.Code == "webhook_backlog").Message);
        Assert.Contains("3 empresas", flags.Single(f => f.Code == "silent_companies").Message);
        Assert.Contains("24 horas", flags.Single(f => f.Code == "silent_companies").Message);
    }

    [Fact]
    public void AnAiUsageThatCouldNotBeRecorded_IsACriticalFlag()
    {
        var flag = Assert.Single(MonitoringFlags.Evaluate(Calm with { LostUsageRecords = 2 }, Thresholds));

        Assert.Equal(("ai_usage_lost", "critical"), (flag.Code, flag.Severity));
        Assert.Contains("2", flag.Message);
    }

    [Fact]
    public void Billing_problems_raise_their_flags_and_nothing_else()
    {
        Assert.DoesNotContain("billing_events_failed", Codes(Calm));
        Assert.DoesNotContain("billing_attempt_stuck", Codes(Calm));

        var codes = Codes(Calm with { BillingEventsFailed = 1, BillingAttemptsStuck = 2, BillingNeedsReview = 1 });

        Assert.Contains("billing_events_failed", codes);
        Assert.Contains("billing_attempt_stuck", codes);
        Assert.Contains("billing_payment_needs_review", codes);
        Assert.Equal(3, codes.Count);
    }

    [Fact]
    public void Failed_message_batches_raise_their_flag_with_the_count()
    {
        Assert.DoesNotContain("llm_batches_failed", Codes(Calm));

        var flag = Assert.Single(MonitoringFlags.Evaluate(Calm with { FailedBatches24h = 2 }, Thresholds), f => f.Code == "llm_batches_failed");

        Assert.Equal("warning", flag.Severity);
        Assert.Contains("2", flag.Message);
    }
}
