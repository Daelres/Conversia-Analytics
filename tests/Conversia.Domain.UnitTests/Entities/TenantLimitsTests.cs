using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The per-company limits the platform administrator sets: the AI switch, the AI quota and the send rate limit.</summary>
public class TenantLimitsTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ANewCompany_HasTheAiOff_AndEveryLimitAtItsDefault()
    {
        var tenant = Tenant.Create("Aurora");

        Assert.False(tenant.AiEnabled);
        Assert.Null(tenant.AiTermsVersion);
        Assert.Null(tenant.AiEnabledChangedAt);
        Assert.Null(tenant.AiMonthlyConversationQuota);
        Assert.Null(tenant.SendRateLimitPerMinute);
    }

    [Fact]
    public void SwitchingTheAiOn_RecordsTheTermsVersionAndTheTime_AndTheSameValueAgainChangesNothing()
    {
        var tenant = Tenant.Create("Aurora");

        Assert.True(tenant.SetAiEnabled(true, " v2026-11 ", Now));
        Assert.True(tenant.AiEnabled);
        Assert.Equal("v2026-11", tenant.AiTermsVersion);
        Assert.Equal(Now, tenant.AiEnabledChangedAt);

        Assert.False(tenant.SetAiEnabled(true, " v2026-11", Now.AddHours(1)));
        Assert.Equal("v2026-11", tenant.AiTermsVersion);
        Assert.Equal(Now, tenant.AiEnabledChangedAt);
    }

    [Fact]
    public void NewTermsWhileTheAiIsOn_AreAChange_NotANoOp()
    {
        var tenant = Tenant.Create("Aurora");
        tenant.SetAiEnabled(true, "v1", Now);

        Assert.True(tenant.SetAiEnabled(true, "v2", Now.AddDays(3)));

        Assert.Equal(("v2", (DateTime?)Now.AddDays(3)), (tenant.AiTermsVersion, tenant.AiEnabledChangedAt));
    }

    [Fact]
    public void SwitchingTheAiOff_KeepsTheTermsVersionAccepted_AsEvidence()
    {
        var tenant = Tenant.Create("Aurora");
        tenant.SetAiEnabled(true, "v1", Now);

        Assert.True(tenant.SetAiEnabled(false, null, Now.AddDays(1)));

        Assert.False(tenant.AiEnabled);
        Assert.Equal("v1", tenant.AiTermsVersion);
        Assert.Equal(Now.AddDays(1), tenant.AiEnabledChangedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SwitchingTheAiOn_WithoutATermsVersion_IsRefused(string? terms)
    {
        var tenant = Tenant.Create("Aurora");

        var error = Assert.Throws<DomainException>(() => tenant.SetAiEnabled(true, terms, Now));

        Assert.Equal(Tenant.AiTermsVersionRuleMessage, error.Message);
        Assert.False(tenant.AiEnabled);
    }

    [Fact]
    public void ATermsVersionLongerThanTheLimit_IsRefused()
        => Assert.Throws<DomainException>(() => Tenant.Create("Aurora").SetAiEnabled(true, new string('v', Tenant.AiTermsVersionMaxLength + 1), Now));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(Tenant.MaxAiMonthlyConversationQuota)]
    public void TheAiQuota_AcceptsNullForTheDefault_AndZeroUpToTheLimit(int? quota)
    {
        var tenant = Tenant.Create("Aurora");

        tenant.SetAiMonthlyConversationQuota(quota);

        Assert.Equal(quota, tenant.AiMonthlyConversationQuota);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Tenant.MaxAiMonthlyConversationQuota + 1)]
    public void TheAiQuota_OutsideItsRange_IsRefused(int quota)
    {
        var error = Assert.Throws<DomainException>(() => Tenant.Create("Aurora").SetAiMonthlyConversationQuota(quota));
        Assert.Equal(Tenant.AiQuotaRuleMessage, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(1200)]
    public void TheSendRateLimit_AcceptsNullForTheDefault_AndOneUpToTheDeploymentsMaximum(int? limit)
    {
        var tenant = Tenant.Create("Aurora");

        tenant.SetSendRateLimitPerMinute(limit, maxPerMinute: 1200);

        Assert.Equal(limit, tenant.SendRateLimitPerMinute);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1201)]
    public void TheSendRateLimit_OutsideItsRange_IsRefused_NamingTheMaximum(int limit)
    {
        var error = Assert.Throws<DomainException>(() => Tenant.Create("Aurora").SetSendRateLimitPerMinute(limit, maxPerMinute: 1200));
        Assert.Equal(Tenant.SendRateLimitRuleMessageFor(1200), error.Message);
        Assert.Contains("1200", error.Message);
    }

    [Fact]
    public void TheDeploymentsMaximum_CannotExceedTheAbsoluteOne()
        => Assert.Throws<DomainException>(() => Tenant.Create("Aurora").SetSendRateLimitPerMinute(Tenant.MaxSendRateLimitPerMinute + 1, Tenant.MaxSendRateLimitPerMinute + 10));

    [Fact]
    public void ALimitChange_NeedsAReason()
    {
        Assert.Equal("Pidió más", Tenant.LimitChangeReason(" Pidió más "));
        Assert.Throws<DomainException>(() => Tenant.LimitChangeReason(null));
        Assert.Throws<DomainException>(() => Tenant.LimitChangeReason(new string('x', Tenant.AiChangeReasonMaxLength + 1)));
    }

    [Fact]
    public void TheAiReason_IsRequiredAndBounded()
    {
        Assert.Equal("Contrato firmado", Tenant.AiChangeReason("  Contrato firmado "));
        Assert.Throws<DomainException>(() => Tenant.AiChangeReason(" "));
        Assert.Throws<DomainException>(() => Tenant.AiChangeReason(new string('x', Tenant.AiChangeReasonMaxLength + 1)));
    }
}
