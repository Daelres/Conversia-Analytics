using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Application.Exceptions;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>What the administrator's service decides; the locking and the queries are checked against PostgreSQL in the integration tests.</summary>
public class PlatformAdminServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPlatformAdminQueries> _queries = new();
    private readonly Mock<ITenantUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITenantChange> _change = new();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly Tenant _tenant = Tenant.Create("Acme");
    private readonly Guid _admin = Guid.NewGuid();
    private int _activeLines;

    public PlatformAdminServiceTests()
    {
        _change.SetupGet(c => c.Tenant).Returns(_tenant);
        _change.Setup(c => c.CountActiveLinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _activeLines);
        _change.Setup(c => c.Record(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(_audit.Add);
        _unitOfWork.Setup(u => u.BeginAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_change.Object);
        _queries.Setup(q => q.GetTenantAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new TenantAdminDetail(
            new TenantAdminSummary(_tenant.Id, _tenant.Name, _tenant.Status, _tenant.MaxActiveLines, _activeLines, _activeLines, "o@x.test", Now, null, null, null, _tenant.SandboxMode), []));
    }

    private readonly RecordingSendLimits _sendLimits = new();

    private PlatformAdminService CreateSut()
        => new(_queries.Object, _unitOfWork.Object, new FixedTimeProvider(Now), new PlatformLimitDefaults(TenantSendRateLimitPerMinute: 120, AiQuotaSettings.Default, MaxSendRateLimitPerMinute: 1200), _sendLimits);

    private sealed class RecordingSendLimits : ITenantSendRateLimits
    {
        public Dictionary<Guid, int?> Set { get; } = [];

        public int? OverrideFor(Guid tenantId) => Set.GetValueOrDefault(tenantId);

        public void Update(Guid tenantId, int? perMinute) => Set[tenantId] = perMinute;
    }

    [Fact]
    public async Task ChangeStatus_PendingToActive_UpdatesTheTenantAuditsAndCommitsOnce()
    {
        var response = await CreateSut().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "active", Reason = "ok" });

        Assert.Equal(TenantStatus.Active, _tenant.Status);
        Assert.Equal((_admin, Now), (_tenant.StatusChangedBy!.Value, _tenant.StatusChangedAt!.Value));
        var entry = Assert.Single(_audit);
        Assert.Equal((_admin, PlatformAuditLog.Actions.TenantStatusChanged, (Guid?)_tenant.Id), (entry.AdminId, entry.Action, entry.TenantId));
        Assert.Contains("\"from\":\"Pending\"", entry.Details);
        Assert.Contains("\"to\":\"Active\"", entry.Details);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(_tenant.Id, response.Id);
    }

    [Fact]
    public async Task ChangeStatus_WithAnInvalidTransition_Throws409AndCommitsNothing()
    {
        await Assert.ThrowsAsync<TenantStatusConflictException>(() =>
            CreateSut().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Pending" }));

        Assert.Empty(_audit);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeStatus_ForAnUnknownTenant_ThrowsNotFound()
    {
        _unitOfWork.Setup(u => u.BeginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ITenantChange?)null);

        await Assert.ThrowsAsync<TenantNotFoundException>(() =>
            CreateSut().ChangeStatusAsync(_admin, Guid.NewGuid(), new ChangeTenantStatusRequest { Status = "Active" }));
    }

    [Fact]
    public async Task ChangeStatus_WithAnUnknownStatusName_IsADomainError()
        => await Assert.ThrowsAsync<DomainException>(() =>
            CreateSut().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Archived" }));

    [Fact]
    public async Task SetLineQuota_BelowTheActiveLines_Throws409WithTheCount()
    {
        _activeLines = 3;

        var failure = await Assert.ThrowsAsync<LineQuotaBelowActiveException>(() =>
            CreateSut().SetLineQuotaAsync(_admin, _tenant.Id, new SetLineQuotaRequest { MaxActiveLines = 2 }));

        Assert.Contains("3", failure.Message);
        Assert.Equal(0, _tenant.MaxActiveLines);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetLineQuota_AtTheActiveLines_IsAllowed_AndAuditsOldAndNew()
    {
        _activeLines = 3;

        await CreateSut().SetLineQuotaAsync(_admin, _tenant.Id, new SetLineQuotaRequest { MaxActiveLines = 3 });

        Assert.Equal(3, _tenant.MaxActiveLines);
        var entry = Assert.Single(_audit);
        Assert.Contains("\"from\":0", entry.Details);
        Assert.Contains("\"to\":3", entry.Details);
    }

    [Fact]
    public async Task SetLineQuota_ToTheSameValue_WritesNothing()
    {
        await CreateSut().SetLineQuotaAsync(_admin, _tenant.Id, new SetLineQuotaRequest { MaxActiveLines = 0 });

        Assert.Empty(_audit);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetLineQuota_WithoutAValue_IsADomainError()
        => await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetLineQuotaAsync(_admin, _tenant.Id, new SetLineQuotaRequest()));

    [Fact]
    public async Task DeactivateChannel_OfAnotherCompanyOrUnknown_IsChannelNotFound()
    {
        _change.Setup(c => c.GetChannelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((WhatsAppChannel?)null);

        await Assert.ThrowsAsync<ChannelNotFoundException>(() => CreateSut().DeactivateChannelAsync(_admin, _tenant.Id, Guid.NewGuid()));
    }

    // ---- Sandbox mode ---------------------------------------------------------------------------

    [Fact]
    public async Task SetSandboxMode_On_UpdatesTheTenantAuditsFromToAndReason_AndCommitsOnce()
    {
        var response = await CreateSut().SetSandboxModeAsync(_admin, _tenant.Id, new SetSandboxModeRequest { Enabled = true, Reason = "  Pruebas del piloto  " });

        Assert.True(_tenant.SandboxMode);
        var entry = Assert.Single(_audit);
        Assert.Equal((_admin, PlatformAuditLog.Actions.TenantSandboxModeChanged, (Guid?)_tenant.Id), (entry.AdminId, entry.Action, entry.TenantId));
        Assert.Equal("tenant.sandbox_mode_changed", entry.Action);
        Assert.Contains("\"from\":false", entry.Details);
        Assert.Contains("\"to\":true", entry.Details);
        Assert.Contains("\"reason\":\"Pruebas del piloto\"", entry.Details);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(response.SandboxMode);
    }

    [Fact]
    public async Task SetSandboxMode_ToWhatItAlreadyIs_WritesNothing()
    {
        await CreateSut().SetSandboxModeAsync(_admin, _tenant.Id, new SetSandboxModeRequest { Enabled = false, Reason = "nada" });

        Assert.False(_tenant.SandboxMode);
        Assert.Empty(_audit);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task SetSandboxMode_WithoutAReason_IsADomainError_AndChangesNothing(string? reason)
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            CreateSut().SetSandboxModeAsync(_admin, _tenant.Id, new SetSandboxModeRequest { Enabled = true, Reason = reason }));

        Assert.False(_tenant.SandboxMode);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task SetSandboxMode_WithoutAValue_IsADomainError()
        => await Assert.ThrowsAsync<DomainException>(() =>
            CreateSut().SetSandboxModeAsync(_admin, _tenant.Id, new SetSandboxModeRequest { Reason = "x" }));

    [Fact]
    public async Task SetSandboxMode_ForAnUnknownTenant_ThrowsNotFound()
    {
        _unitOfWork.Setup(u => u.BeginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ITenantChange?)null);

        await Assert.ThrowsAsync<TenantNotFoundException>(() =>
            CreateSut().SetSandboxModeAsync(_admin, Guid.NewGuid(), new SetSandboxModeRequest { Enabled = true, Reason = "x" }));
    }

    // ---- AI switch, AI quota and send rate limit --------------------------------------------------

    [Fact]
    public async Task SetAiEnabled_On_RecordsTheTermsAndAuditsFromToReasonAndTerms_AsConsentEvidence()
    {
        await CreateSut().SetAiEnabledAsync(_admin, _tenant.Id, new SetAiEnabledRequest { Enabled = true, Reason = " Contrato firmado ", TermsVersion = "ia-2026-11" });

        Assert.True(_tenant.AiEnabled);
        Assert.Equal("ia-2026-11", _tenant.AiTermsVersion);
        var entry = Assert.Single(_audit);
        Assert.Equal((PlatformAuditLog.Actions.TenantAiEnabledChanged, (Guid?)_tenant.Id), (entry.Action, entry.TenantId));
        Assert.Equal("tenant.ai_enabled_changed", entry.Action);
        Assert.Contains("\"from\":false", entry.Details);
        Assert.Contains("\"to\":true", entry.Details);
        Assert.Contains("\"reason\":\"Contrato firmado\"", entry.Details);
        Assert.Contains("\"termsVersion\":\"ia-2026-11\"", entry.Details);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAiEnabled_Off_NeedsAReasonButNoTerms()
    {
        _tenant.SetAiEnabled(true, "v1", Now);

        await CreateSut().SetAiEnabledAsync(_admin, _tenant.Id, new SetAiEnabledRequest { Enabled = false, Reason = "Pidió pausar" });

        Assert.False(_tenant.AiEnabled);
        Assert.Contains("\"termsVersion\":null", Assert.Single(_audit).Details);
    }

    [Theory]
    [InlineData(true, null, "v1")]
    [InlineData(true, "Motivo", null)]
    [InlineData(null, "Motivo", "v1")]
    public async Task SetAiEnabled_WithoutTheValueTheReasonOrTheTerms_ChangesNothing(bool? enabled, string? reason, string? terms)
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            CreateSut().SetAiEnabledAsync(_admin, _tenant.Id, new SetAiEnabledRequest { Enabled = enabled, Reason = reason, TermsVersion = terms }));

        Assert.False(_tenant.AiEnabled);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task SetAiEnabled_ToWhatItAlreadyIs_WritesNothing()
    {
        await CreateSut().SetAiEnabledAsync(_admin, _tenant.Id, new SetAiEnabledRequest { Enabled = false, Reason = "nada" });

        Assert.Empty(_audit);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAiQuota_SetsAndResets_AuditingFromAndTo()
    {
        await CreateSut().SetAiQuotaAsync(_admin, _tenant.Id, new SetAiQuotaRequest { MonthlyConversations = 1200, Reason = "Plan ampliado" });
        await CreateSut().SetAiQuotaAsync(_admin, _tenant.Id, new SetAiQuotaRequest { MonthlyConversations = null, Reason = "Vuelve al plan base" });

        Assert.Null(_tenant.AiMonthlyConversationQuota);
        Assert.Equal(2, _audit.Count);
        Assert.All(_audit, e => Assert.Equal("tenant.ai_quota_changed", e.Action));
        Assert.Contains("\"from\":null", _audit[0].Details);
        Assert.Contains("\"to\":1200", _audit[0].Details);
        Assert.Contains("\"reason\":\"Plan ampliado\"", _audit[0].Details);
        Assert.Contains("\"to\":null", _audit[1].Details);
    }

    [Fact]
    public async Task SetAiQuota_ToTheSameValue_WritesNothing()
    {
        await CreateSut().SetAiQuotaAsync(_admin, _tenant.Id, new SetAiQuotaRequest { MonthlyConversations = null, Reason = "Vuelve al plan base" });

        Assert.Empty(_audit);
    }

    [Fact]
    public async Task SetSendRateLimit_AuditsAndTellsTheLimiterOfThisInstanceAtOnce()
    {
        await CreateSut().SetSendRateLimitAsync(_admin, _tenant.Id, new SetSendRateLimitRequest { PerMinute = 30, Reason = "Campaña" });

        Assert.Equal(30, _tenant.SendRateLimitPerMinute);
        var entry = Assert.Single(_audit);
        Assert.Equal("tenant.send_rate_limit_changed", entry.Action);
        Assert.Contains("\"to\":30", entry.Details);
        Assert.Equal(30, _sendLimits.Set[_tenant.Id]);
    }

    [Fact]
    public async Task SetSendRateLimit_OutOfRange_IsADomainError()
        => await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetSendRateLimitAsync(_admin, _tenant.Id, new SetSendRateLimitRequest { PerMinute = 0, Reason = "x" }));

    [Fact]
    public async Task TheDetail_ShowsTheEffectiveLimits_AndWhetherEachIsTheDefault()
    {
        _queries.Setup(q => q.GetTenantAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new TenantAdminDetail(
            new TenantAdminSummary(_tenant.Id, _tenant.Name, _tenant.Status, 4, 2, 3, "o@x.test", Now, null, null, null, false,
                AiEnabled: true, AiTermsVersion: "v1", AiEnabledChangedAt: Now, AiMonthlyConversationQuota: null, SendRateLimitPerMinute: 45), []));
        _queries.Setup(q => q.ListAuditAsync(_tenant.Id, 1, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReadPage<PlatformAuditEntry>([], 0));

        var detail = await CreateSut().GetTenantAsync(_tenant.Id);

        var limits = detail.Limits;
        Assert.Equal((4, 2), (limits.MaxActiveLines, limits.ActiveLines));
        Assert.Equal((true, "v1"), (limits.AiEnabled, limits.AiTermsVersion));
        Assert.Equal((500, true, 10m), (limits.AiMonthlyConversationQuota, limits.AiQuotaIsDefault, limits.AiMonthlyUsdCeiling));
        Assert.Equal((45, false, 120), (limits.SendRateLimitPerMinute, limits.SendRateLimitIsDefault, limits.DefaultSendRateLimitPerMinute));
        Assert.True(detail.AiEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task ALimitChange_WithoutAReason_ChangesNothing(string? reason)
    {
        await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetAiQuotaAsync(_admin, _tenant.Id, new SetAiQuotaRequest { MonthlyConversations = 9, Reason = reason }));
        await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetSendRateLimitAsync(_admin, _tenant.Id, new SetSendRateLimitRequest { PerMinute = 9, Reason = reason }));

        Assert.Equal((null, null), (_tenant.AiMonthlyConversationQuota, _tenant.SendRateLimitPerMinute));
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task TheSendLimit_CannotGoAboveTheDeploymentsMaximum()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            CreateSut().SetSendRateLimitAsync(_admin, _tenant.Id, new SetSendRateLimitRequest { PerMinute = 1201, Reason = "Mucho" }));

        Assert.Contains("1200", error.Message);
        Assert.Null(_tenant.SendRateLimitPerMinute);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task NewTermsWhileTheAiIsOn_AreAudited_WithThePreviousVersion()
    {
        _tenant.SetAiEnabled(true, "v1", Now);

        await CreateSut().SetAiEnabledAsync(_admin, _tenant.Id, new SetAiEnabledRequest { Enabled = true, Reason = "Nuevos términos", TermsVersion = "v2" });

        var entry = Assert.Single(_audit);
        Assert.Contains("\"from\":true", entry.Details);
        Assert.Contains("\"termsVersion\":\"v2\"", entry.Details);
        Assert.Contains("\"previousTermsVersion\":\"v1\"", entry.Details);
    }
}
