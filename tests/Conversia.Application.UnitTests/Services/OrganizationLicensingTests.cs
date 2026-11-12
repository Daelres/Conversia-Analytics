using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Licensing;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>
/// A company's lines and the platform's licences (docs/PLATFORM_ADMIN.md, "Line licences"): the matching licence is linked when the line is
/// registered or switched on; with <c>Licensing:RequireAssignedLicense</c> a line without one cannot be switched on.
/// </summary>
public class OrganizationLicensingTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IBranchRepository> _branches = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<ITenantUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITenantChange> _change = new();
    private readonly Mock<ILineLicenseRepository> _licenses = new();
    private readonly List<LineLicense> _assigned = [];
    private readonly List<WhatsAppChannel> _companyLines = [];
    private readonly Tenant _tenant = Tenant.Create("Aurora");
    private readonly Branch _branch;

    public OrganizationLicensingTests()
    {
        _tenant.ChangeStatus(TenantStatus.Active, Guid.NewGuid(), null, Now);
        _tenant.SetMaxActiveLines(5);
        _branch = Branch.Create(_tenant.Id, "Centro");
        _branches.Setup(r => r.GetByIdAsync(_branch.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_branch);
        _change.SetupGet(c => c.Tenant).Returns(_tenant);
        _change.Setup(c => c.GetAssignedLicensesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _assigned);
        _change.Setup(c => c.GetChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _companyLines);
        _change.Setup(c => c.GetChannelAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _companyLines.FirstOrDefault(c => c.Id == id));
        _unitOfWork.Setup(u => u.BeginAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_change.Object);
    }

    private OrganizationService CreateSut(bool requireAssignedLicense)
        => new(_branches.Object, new Mock<IAdvisorRepository>().Object, _channels.Object, new Mock<IUserRepository>().Object, _unitOfWork.Object,
            new Mock<ISecretProtector>().Object, new FixedTimeProvider(Now), _licenses.Object, new LicensingSettings(requireAssignedLicense));

    private WhatsAppChannel Line(string phone, bool active = false)
    {
        var (channel, _) = WhatsAppChannel.Create(_tenant.Id, _branch.Id, phone);
        if (active)
        {
            channel.Activate();
        }

        _companyLines.Add(channel);
        return channel;
    }

    private LineLicense Assigned(string phone)
    {
        var license = LineLicense.Create(phone, null, 10m, false, null);
        license.AssignTo(_tenant.Id, Now);
        _assigned.Add(license);
        return license;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingOnALine_LinksTheMatchingAssignedLicence(bool required)
    {
        var line = Line("+57 300 555 1234");
        var license = Assigned("573005551234");

        var response = await CreateSut(required).SetChannelActiveAsync(_tenant.Id, line.Id, true);

        Assert.True(response.IsActive);
        Assert.Equal(line.Id, license.ChannelId);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithoutTheSwitch_ALineWithoutLicence_IsSwitchedOnAsBefore()
    {
        var line = Line("+57 300 555 1234");

        var response = await CreateSut(requireAssignedLicense: false).SetChannelActiveAsync(_tenant.Id, line.Id, true);

        Assert.True(response.IsActive);
    }

    [Fact]
    public async Task WithTheSwitch_ALineWithoutAMatchingLicence_IsRefused_WithTheStableCode()
    {
        var line = Line("+57 300 555 1234");
        Assigned("+57 300 555 9999");

        var error = await Assert.ThrowsAsync<LicenseRequiredException>(() => CreateSut(requireAssignedLicense: true).SetChannelActiveAsync(_tenant.Id, line.Id, true));

        Assert.Equal("license_required", LicenseRequiredException.Code);
        Assert.Contains("licencia", error.Message);
        Assert.False(line.IsActive);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithTheSwitch_ALicenceAlreadyUsedByAnotherActiveLine_DoesNotCountTwice()
    {
        var first = Line("+57 300 555 1234", active: true);
        var second = Line("+57 300 555 1234");
        Assigned("+57 300 555 1234").LinkChannel(first.Id);

        await Assert.ThrowsAsync<LicenseRequiredException>(() => CreateSut(requireAssignedLicense: true).SetChannelActiveAsync(_tenant.Id, second.Id, true));
    }

    [Fact]
    public async Task WithTheSwitch_ALicenceLinkedToALineThatIsOff_MovesToTheLineSwitchedOn()
    {
        var old = Line("+57 300 555 1234", active: false);
        var replacement = Line("+57 300 555 1234");
        var license = Assigned("+57 300 555 1234");
        license.LinkChannel(old.Id);

        await CreateSut(requireAssignedLicense: true).SetChannelActiveAsync(_tenant.Id, replacement.Id, true);

        Assert.Equal(replacement.Id, license.ChannelId);
        Assert.True(replacement.IsActive);
    }

    [Fact]
    public async Task RegisteringALine_LinksAnUnlinkedAssignedLicenceOfTheCompany()
    {
        var license = Assigned("+57 300 555 1234");
        _licenses.Setup(r => r.FindAssignedMatchAsync(_tenant.Id, "+57 300 555 1234", null, It.IsAny<CancellationToken>())).ReturnsAsync(license);

        var created = await CreateSut(requireAssignedLicense: false).CreateChannelAsync(_tenant.Id, new CreateChannelRequest { BranchId = _branch.Id, DisplayPhoneNumber = "+57 300 555 1234" });

        Assert.Equal(created.Id, license.ChannelId);
        _licenses.Verify(r => r.SaveAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisteringALine_IsNotHeldUpByARaceOnTheLicence()
    {
        var license = Assigned("+57 300 555 1234");
        _licenses.Setup(r => r.FindAssignedMatchAsync(_tenant.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(license);
        _licenses.Setup(r => r.SaveAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(new ConcurrencyConflictException());

        var created = await CreateSut(requireAssignedLicense: false).CreateChannelAsync(_tenant.Id, new CreateChannelRequest { BranchId = _branch.Id, DisplayPhoneNumber = "+57 300 555 1234" });

        Assert.NotEqual(Guid.Empty, created.Id);
    }
}
