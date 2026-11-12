using Conversia.Application.DTOs.Admin;
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

    private PlatformAdminService CreateSut() => new(_queries.Object, _unitOfWork.Object, new FixedTimeProvider(Now));

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
}
