using Conversia.Application.Classification.Llm;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>The quota arithmetic and the admission order (design D12, D13, D18): pure, no database.</summary>
public class AiQuotaTests
{
    private static readonly AiQuotaSettings Settings = AiQuotaSettings.Default;

    private static AiAdmissionFacts Facts(
        bool enabled = true, bool sandbox = false, int quota = 500, int analyzed = 0, decimal tenantUsd = 0m, decimal deploymentUsd = 0m, decimal cap = 20m,
        decimal sandboxUsd = 0m)
        => new(enabled, sandbox, quota, analyzed, tenantUsd, Settings.CeilingUsd(quota), deploymentUsd, cap, sandboxUsd, Settings.SandboxMonthlyUsdPerCompany);

    [Fact]
    public void TheDefaults_AreTheDesignOnes()
    {
        Assert.Equal(500, Settings.DefaultMonthlyConversations);
        Assert.Equal(0.02m, Settings.UsdPerQuotaUnit);
        Assert.Equal(20m, Settings.DeploymentMonthlyUsdCap);
        Assert.Equal(2m, Settings.SandboxMonthlyUsdPerCompany);
    }

    [Fact]
    public void ACompanysSandbox_HasItsOwnUsdBound_SoItCannotSpendTheDeploymentCap()
    {
        Assert.Equal(AiAdmission.Allowed, AiAdmissionPolicy.Decide(Facts(sandbox: true, sandboxUsd: 1.99m)));
        Assert.Equal(AiAdmission.SandboxUsdCeiling, AiAdmissionPolicy.Decide(Facts(sandbox: true, sandboxUsd: 2m)));
        Assert.Equal(AiAdmission.Allowed, AiAdmissionPolicy.Decide(Facts(sandbox: false, sandboxUsd: 50m))); // real work is not held up by tests
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData(0, 0)]
    [InlineData(1200, 1200)]
    public void TheEffectiveQuota_IsTheCompanysOrTheDefault(int? own, int effective)
        => Assert.Equal(effective, Settings.EffectiveQuota(own));

    [Fact]
    public void TheCeiling_IsTheUsdPerUnitTimesTheQuota()
        => Assert.Equal(10m, Settings.CeilingUsd(500));

    [Theory]
    [InlineData("2026-11-01T04:59:59Z", "2026-10-01")] // 23:59:59 on 31 Oct in Bogota
    [InlineData("2026-11-01T05:00:00Z", "2026-11-01")] // midnight in Bogota
    [InlineData("2026-12-31T23:00:00Z", "2026-12-01")]
    public void TheMonth_IsTheBogotaMonth(string utc, string month)
        => Assert.Equal(DateOnly.Parse(month, System.Globalization.CultureInfo.InvariantCulture),
            Settings.MonthOf(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)));

    [Fact]
    public void ThePreviousMonth_IsKeptForTheDistinctSets()
        => Assert.Equal(new DateOnly(2026, 10, 1), AiQuotaSettings.PreviousMonth(new DateOnly(2026, 11, 1)));

    [Fact]
    public void AnEnabledCompanyWithinEverything_IsAdmitted()
        => Assert.Equal(AiAdmission.Allowed, AiAdmissionPolicy.Decide(Facts(analyzed: 499, tenantUsd: 9.99m, deploymentUsd: 19.99m)));

    [Fact]
    public void TheSwitch_ComesFirst()
        => Assert.Equal(AiAdmission.AiDisabled, AiAdmissionPolicy.Decide(Facts(enabled: false, analyzed: 9999, deploymentUsd: 99m)));

    [Fact]
    public void TheDeploymentCap_ComesBeforeTheCompanysOwnLimits_AndAppliesToSandboxToo()
    {
        Assert.Equal(AiAdmission.DeploymentCapReached, AiAdmissionPolicy.Decide(Facts(analyzed: 9999, deploymentUsd: 20m)));
        Assert.Equal(AiAdmission.DeploymentCapReached, AiAdmissionPolicy.Decide(Facts(sandbox: true, deploymentUsd: 20m)));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(0, 0)]
    public void AQuotaUsedUp_OrZero_IsExhausted(int quota, int analyzed)
        => Assert.Equal(AiAdmission.TenantExhausted, AiAdmissionPolicy.Decide(Facts(quota: quota, analyzed: analyzed)));

    [Fact]
    public void TheCompanysUsdCeiling_StopsItEvenWithQuotaLeft()
        => Assert.Equal(AiAdmission.TenantUsdCeiling, AiAdmissionPolicy.Decide(Facts(analyzed: 10, tenantUsd: 10m)));

    [Fact]
    public void ASandboxConversation_IsNotStoppedByTheBillableQuotaOrCeiling()
        => Assert.Equal(AiAdmission.Allowed, AiAdmissionPolicy.Decide(Facts(sandbox: true, quota: 0, analyzed: 9999, tenantUsd: 99m)));

    [Theory]
    [InlineData(false, 0, 500, 0, AiUsageState.Disabled)]
    [InlineData(true, 0, 500, 0, AiUsageState.Ok)]
    [InlineData(true, 399, 500, 0, AiUsageState.Ok)]
    [InlineData(true, 400, 500, 0, AiUsageState.Warning)]
    [InlineData(true, 500, 500, 0, AiUsageState.Exhausted)]
    [InlineData(true, 0, 0, 0, AiUsageState.Exhausted)]
    [InlineData(true, 10, 500, 10, AiUsageState.Exhausted)]
    [InlineData(true, 10, 500, 8, AiUsageState.Warning)]
    public void TheUsageState_TellsTheAdministratorWhereTheCompanyStands(bool enabled, int used, int quota, double usd, AiUsageState state)
        => Assert.Equal(state, AiUsageStates.Of(enabled, used, quota, (decimal)usd, Settings.CeilingUsd(quota)));
}
