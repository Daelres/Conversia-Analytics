using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>How long a company's stored media is kept (docs/PLATFORM_ADMIN.md, "Media retention").</summary>
public class MediaRetentionTests
{
    [Fact]
    public void ANewCompany_UsesThePlatformDefault()
        => Assert.Null(Tenant.Create("Aurora").MediaRetentionDays);

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(Tenant.MaxMediaRetentionDays)]
    public void TheAdministratorCanSetItsOwnRetention(int days)
    {
        var tenant = Tenant.Create("Aurora");

        tenant.SetMediaRetentionDays(days);

        Assert.Equal(days, tenant.MediaRetentionDays);
    }

    [Fact]
    public void ClearingIt_GoesBackToTheDefault()
    {
        var tenant = Tenant.Create("Aurora");
        tenant.SetMediaRetentionDays(30);

        tenant.SetMediaRetentionDays(null);

        Assert.Null(tenant.MediaRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Tenant.MaxMediaRetentionDays + 1)]
    public void AnOutOfRangeRetention_IsRefused(int days)
        => Assert.Throws<DomainException>(() => Tenant.Create("Aurora").SetMediaRetentionDays(days));

    [Fact]
    public void AnObjectToDelete_NamesItsCompanyAndKey()
    {
        var tenantId = Guid.NewGuid();
        var deletion = MediaObjectDeletion.Create(tenantId, $"{tenantId:N}/2026/11/abc", new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(tenantId, deletion.TenantId);
        Assert.Throws<DomainException>(() => MediaObjectDeletion.Create(tenantId, "", DateTime.UtcNow));
    }
}
