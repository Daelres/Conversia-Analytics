using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>A 360dialog line licence of the platform's inventory: Available, Assigned to one company, Retired.</summary>
public class LineLicenseTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

    private static LineLicense New(string phone = "+57 300 555 1234", decimal cost = 12.5m, bool customerOwned = false, string? providerId = "ch-01", string? notes = null)
        => LineLicense.Create(phone, providerId, cost, customerOwned, notes);

    [Fact]
    public void ANewLicence_IsAvailable_For360dialog_WithTheDigitsOfTheNumber()
    {
        var license = New(notes: "  Lote noviembre ");

        Assert.Equal(LineLicense.Dialog360, license.Provider);
        Assert.Equal("573005551234", license.PhoneDigits);
        Assert.Equal("ch-01", license.ProviderChannelId);
        Assert.Equal(12.5m, license.MonthlyCostUsd);
        Assert.False(license.CustomerOwned);
        Assert.Equal(LineLicenseStatus.Available, license.Status);
        Assert.Null(license.AssignedTenantId);
        Assert.Null(license.ChannelId);
        Assert.Equal("Lote noviembre", license.Notes);
    }

    [Fact]
    public void TheNumberIsShownMasked_ExceptItsLastFourDigits()
    {
        Assert.Equal("•••• 1234", New().MaskedPhoneNumber);
        Assert.Equal("•••• 4567", LineLicense.Mask("1234567"));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("+57 300 abc 1234")]
    [InlineData("")]
    public void AnInvalidNumber_IsRefused(string phone)
        => Assert.Throws<DomainException>(() => New(phone));

    [Theory]
    [InlineData(-1)]
    [InlineData(10_000.01)]
    public void ACostOutsideItsRange_IsRefused(double cost)
        => Assert.Throws<DomainException>(() => New(cost: (decimal)cost));

    [Fact]
    public void ACustomerOwnedLicence_CostsNothingToTheReport()
    {
        Assert.Equal(0m, New(cost: 0m, customerOwned: true).MonthlyCostUsd);
        var error = Assert.Throws<DomainException>(() => New(cost: 5m, customerOwned: true));
        Assert.Equal(LineLicense.CustomerOwnedCostRuleMessage, error.Message);
    }

    [Fact]
    public void AnInvalidProviderId_IsRefused()
        => Assert.Throws<DomainException>(() => New(providerId: "no válido!"));

    [Fact]
    public void Assigning_ThenReleasing_ThenRetiring_FollowsTheLifecycle()
    {
        var license = New();
        var tenant = Guid.NewGuid();

        license.AssignTo(tenant, Now);
        Assert.Equal((LineLicenseStatus.Assigned, tenant, Now), (license.Status, license.AssignedTenantId, license.AssignedAt));

        license.LinkChannel(Guid.NewGuid());
        license.Release(Now.AddDays(1));
        Assert.Equal(LineLicenseStatus.Available, license.Status);
        Assert.Null(license.AssignedTenantId);
        Assert.Null(license.ChannelId);
        Assert.Equal(Now.AddDays(1), license.ReleasedAt);

        license.Retire(Now.AddDays(2));
        Assert.Equal(LineLicenseStatus.Retired, license.Status);
        Assert.Equal(Now.AddDays(2), license.RetiredAt);
    }

    [Fact]
    public void OnlyAnAvailableLicence_CanBeAssignedOrRetired_AndOnlyAnAssignedOne_Released()
    {
        var assigned = New();
        assigned.AssignTo(Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => assigned.AssignTo(Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => assigned.Retire(Now));

        var available = New();
        Assert.Throws<DomainException>(() => available.Release(Now));

        var retired = New();
        retired.Retire(Now);
        Assert.Throws<DomainException>(() => retired.AssignTo(Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => retired.Retire(Now));
    }

    [Fact]
    public void OnlyAnAssignedLicence_IsLinkedToAChannel()
    {
        var license = New();
        Assert.Throws<DomainException>(() => license.LinkChannel(Guid.NewGuid()));

        license.AssignTo(Guid.NewGuid(), Now);
        var channel = Guid.NewGuid();
        license.LinkChannel(channel);
        Assert.Equal(channel, license.ChannelId);
    }

    [Theory]
    [InlineData("+57 300 555 1234", null, true)]
    [InlineData("573005551234", null, true)]
    [InlineData("+57 300 555 9999", null, false)]
    [InlineData("+57 300 555 9999", "ch-01", true)]
    public void ALicenceMatchesAChannel_ByTheDigitsOfTheNumber_OrByTheProviderId(string channelPhone, string? channelProviderId, bool matches)
        => Assert.Equal(matches, New().Matches(channelPhone, channelProviderId));

    [Fact]
    public void ALicenceWithoutProviderId_NeverMatchesByAMissingOne()
        => Assert.False(New(providerId: null).Matches("+57 300 555 9999", null));
}
