using Conversia.Application.DTOs.Admin;
using Conversia.Application.Exceptions;
using Conversia.Application.Licensing;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>The licence inventory's rules (docs/PLATFORM_ADMIN.md, "Line licences"); persistence and races are in the integration tests.</summary>
public class LineLicenseServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILineLicenseRepository> _repository = new();
    private readonly Mock<ITenantUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITenantChange> _change = new();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly List<WhatsAppChannel> _channels = [];
    private readonly Tenant _tenant = Tenant.Create("Aurora");
    private readonly Guid _admin = Guid.NewGuid();
    private readonly LineLicense _license = LineLicense.Create("+57 300 555 1234", "ch-01", 15m, false, null);

    public LineLicenseServiceTests()
    {
        _repository.Setup(r => r.GetAsync(_license.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_license);
        _repository.Setup(r => r.SaveAsync(It.IsAny<PlatformAuditLog?>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformAuditLog?, CancellationToken>((e, _) => { if (e is not null) { _audit.Add(e); } })
            .Returns(Task.CompletedTask);
        _change.SetupGet(c => c.Tenant).Returns(_tenant);
        _change.Setup(c => c.Record(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(_audit.Add);
        _change.Setup(c => c.GetChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _channels);
        _change.Setup(c => c.GetAssignedLicensesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _license.AssignedTenantId == _tenant.Id ? [_license] : []);
        _unitOfWork.Setup(u => u.BeginAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_change.Object);
    }

    private LineLicenseService CreateSut(bool requireAssignedLicense = false)
        => new(_repository.Object, _unitOfWork.Object, new LicensingSettings(requireAssignedLicense), new FixedTimeProvider(Now));

    private WhatsAppChannel Channel(string phone, bool active)
    {
        var (channel, _) = WhatsAppChannel.Create(_tenant.Id, Guid.NewGuid(), phone);
        if (active)
        {
            channel.Activate();
        }

        _channels.Add(channel);
        return channel;
    }

    [Fact]
    public async Task Create_AddsAnAvailableLicence_AndAuditsItWithTheNumberMasked()
    {
        LineLicense? added = null;
        _repository.Setup(r => r.Add(It.IsAny<LineLicense>())).Callback<LineLicense>(l => added = l);

        var response = await CreateSut().CreateAsync(_admin, new CreateLineLicenseRequest
        {
            PhoneNumber = "+57 310 222 9876", ProviderChannelId = "abc", MonthlyCostUsd = 12.50m, Notes = "Lote 1",
        });

        Assert.NotNull(added);
        Assert.Equal(("•••• 9876", "Available", 12.50m), (response.PhoneNumberMasked, response.Status, response.MonthlyCostUsd));
        var entry = Assert.Single(_audit);
        Assert.Equal(("license.created", (Guid?)null), (entry.Action, entry.TenantId));
        Assert.Contains("•••• 9876", entry.Details);
        Assert.DoesNotContain("3102229876", entry.Details);
        Assert.DoesNotContain("310 222", entry.Details);
    }

    [Fact]
    public async Task Create_WithoutACost_IsADomainError()
        => await Assert.ThrowsAsync<DomainException>(() => CreateSut().CreateAsync(_admin, new CreateLineLicenseRequest { PhoneNumber = "+57 310 222 9876" }));

    [Fact]
    public async Task Assign_GivesTheLicenceToTheCompany_LinksTheMatchingLine_AndAudits()
    {
        var other = Channel("+57 300 555 0000", active: true);
        var matching = Channel("+57 300 555 1234", active: true);

        var response = await CreateSut().AssignAsync(_admin, _license.Id, new AssignLineLicenseRequest { TenantId = _tenant.Id });

        Assert.Equal((LineLicenseStatus.Assigned, (Guid?)_tenant.Id, (Guid?)matching.Id), (_license.Status, _license.AssignedTenantId, _license.ChannelId));
        Assert.NotEqual(other.Id, _license.ChannelId);
        var entry = Assert.Single(_audit);
        Assert.Equal(("license.assigned", (Guid?)_tenant.Id), (entry.Action, entry.TenantId));
        Assert.Contains(_license.Id.ToString(), entry.Details);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Assigned", response.Status);
    }

    [Fact]
    public async Task Assign_ALicenceThatIsNotAvailable_Is409()
    {
        _license.AssignTo(Guid.NewGuid(), Now);

        var error = await Assert.ThrowsAsync<LineLicenseConflictException>(() =>
            CreateSut().AssignAsync(_admin, _license.Id, new AssignLineLicenseRequest { TenantId = _tenant.Id }));

        Assert.Equal(LineLicenseConflictException.NotAvailable, error.Code);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task Assign_ToAnUnknownCompany_IsTenantNotFound_AndAnUnknownLicence_IsLicenceNotFound()
    {
        await Assert.ThrowsAsync<TenantNotFoundException>(() => CreateSut().AssignAsync(_admin, _license.Id, new AssignLineLicenseRequest { TenantId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<LineLicenseNotFoundException>(() => CreateSut().AssignAsync(_admin, Guid.NewGuid(), new AssignLineLicenseRequest { TenantId = _tenant.Id }));
    }

    [Fact]
    public async Task Release_ReturnsTheLicenceToTheInventory_AndLeavesTheLineOn_WhenALicenceIsNotRequired()
    {
        var line = Channel("+57 300 555 1234", active: true);
        _license.AssignTo(_tenant.Id, Now);
        _license.LinkChannel(line.Id);

        await CreateSut(requireAssignedLicense: false).ReleaseAsync(_admin, _license.Id);

        Assert.Equal((LineLicenseStatus.Available, (Guid?)null), (_license.Status, _license.AssignedTenantId));
        Assert.True(line.IsActive);
        var entry = Assert.Single(_audit);
        Assert.Equal(("license.released", (Guid?)_tenant.Id), (entry.Action, entry.TenantId));
        Assert.Contains("\"lineDeactivated\":false", entry.Details);
    }

    [Fact]
    public async Task Release_SwitchesTheLinkedLineOff_WhenALicenceIsRequired()
    {
        var line = Channel("+57 300 555 1234", active: true);
        _license.AssignTo(_tenant.Id, Now);
        _license.LinkChannel(line.Id);

        await CreateSut(requireAssignedLicense: true).ReleaseAsync(_admin, _license.Id);

        Assert.False(line.IsActive);
        Assert.Contains("\"lineDeactivated\":true", Assert.Single(_audit).Details);
    }

    [Fact]
    public async Task Release_ALicenceThatIsNotAssigned_Is409()
    {
        var error = await Assert.ThrowsAsync<LineLicenseConflictException>(() => CreateSut().ReleaseAsync(_admin, _license.Id));
        Assert.Equal(LineLicenseConflictException.NotAssigned, error.Code);
    }

    [Fact]
    public async Task Retire_OnlyAnAvailableLicence_AndAuditsIt()
    {
        await CreateSut().RetireAsync(_admin, _license.Id);
        Assert.Equal(LineLicenseStatus.Retired, _license.Status);
        Assert.Equal("license.retired", Assert.Single(_audit).Action);

        var assigned = LineLicense.Create("+57 300 555 7777", null, 0m, true, null);
        assigned.AssignTo(_tenant.Id, Now);
        _repository.Setup(r => r.GetAsync(assigned.Id, It.IsAny<CancellationToken>())).ReturnsAsync(assigned);
        var error = await Assert.ThrowsAsync<LineLicenseConflictException>(() => CreateSut().RetireAsync(_admin, assigned.Id));
        Assert.Equal(LineLicenseConflictException.StillAssigned, error.Code);
    }
}
