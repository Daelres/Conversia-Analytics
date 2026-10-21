using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class TenantTests
{
    [Fact]
    public void Create_WithValidName_ReturnsTenantWithNonEmptyIdAndGivenName()
    {
        var tenant = Tenant.Create("Acme Corp");

        Assert.NotEqual(Guid.Empty, tenant.Id);
        Assert.Equal("Acme Corp", tenant.Name);
    }

    [Fact]
    public void Create_WithANulCharacter_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Tenant.Create("Acme\0Corp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyOrWhitespaceName_ThrowsDomainException(string? name)
    {
        Assert.Throws<DomainException>(() => Tenant.Create(name!));
    }

    [Fact]
    public void Create_WithNameExceedingMaxLength_ThrowsDomainException()
    {
        var tooLongName = new string('a', 201);

        Assert.Throws<DomainException>(() => Tenant.Create(tooLongName));
    }

    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ANewTenant_IsPending_WithNoLinesAndNoStatusChange()
    {
        var tenant = Tenant.Create("Acme Corp");

        Assert.Equal(TenantStatus.Pending, tenant.Status);
        Assert.Equal(0, tenant.MaxActiveLines);
        Assert.Null(tenant.StatusChangedAt);
        Assert.Null(tenant.StatusChangedBy);
        Assert.Null(tenant.StatusReason);
    }

    [Theory]
    [InlineData(TenantStatus.Pending, TenantStatus.Active, true)]
    [InlineData(TenantStatus.Pending, TenantStatus.Suspended, true)]
    [InlineData(TenantStatus.Active, TenantStatus.Suspended, true)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Active, true)]
    [InlineData(TenantStatus.Active, TenantStatus.Pending, false)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Pending, false)]
    [InlineData(TenantStatus.Pending, TenantStatus.Pending, false)]
    [InlineData(TenantStatus.Active, TenantStatus.Active, false)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Suspended, false)]
    public void TheStatusTransitions_AreExactlyTheDocumentedOnes(TenantStatus from, TenantStatus to, bool allowed)
        => Assert.Equal(allowed, Tenant.CanChangeStatus(from, to));

    [Fact]
    public void ChangeStatus_RecordsWhoWhenAndWhy_AndTrimsTheReason()
    {
        var tenant = Tenant.Create("Acme Corp");
        var admin = Guid.NewGuid();

        tenant.ChangeStatus(TenantStatus.Active, admin, "  Contract signed  ", Now);

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(admin, tenant.StatusChangedBy);
        Assert.Equal(Now, tenant.StatusChangedAt);
        Assert.Equal("Contract signed", tenant.StatusReason);
    }

    [Fact]
    public void ChangeStatus_WithAnInvalidTransition_ThrowsAndChangesNothing()
    {
        var tenant = Tenant.Create("Acme Corp");

        Assert.Throws<DomainException>(() => tenant.ChangeStatus(TenantStatus.Pending, Guid.NewGuid(), null, Now));

        Assert.Equal(TenantStatus.Pending, tenant.Status);
        Assert.Null(tenant.StatusChangedAt);
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("surrogate")]
    public void ChangeStatus_WithAnUnstorableReason_ThrowsBeforeAssigningAnything(string kind)
    {
        // Built here: xunit would mangle a lone surrogate passed as theory data.
        var reason = kind == "nul" ? "bad\0reason" : "lone \ud800 surrogate";
        var tenant = Tenant.Create("Acme Corp");

        Assert.Throws<DomainException>(() => tenant.ChangeStatus(TenantStatus.Active, Guid.NewGuid(), reason, Now));
        Assert.Throws<DomainException>(() => tenant.ChangeStatus(TenantStatus.Active, Guid.NewGuid(), new string('x', 501), Now));

        Assert.Equal(TenantStatus.Pending, tenant.Status);
        Assert.Null(tenant.StatusChangedBy);
    }

    [Fact]
    public void ChangeStatus_RequiresTheAdministrator()
        => Assert.Throws<DomainException>(() => Tenant.Create("Acme Corp").ChangeStatus(TenantStatus.Active, Guid.Empty, null, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1000)]
    public void SetMaxActiveLines_AcceptsTheWholeRange(int quota)
    {
        var tenant = Tenant.Create("Acme Corp");

        tenant.SetMaxActiveLines(quota);

        Assert.Equal(quota, tenant.MaxActiveLines);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void SetMaxActiveLines_RefusesWhatIsOutsideTheRange(int quota)
    {
        var tenant = Tenant.Create("Acme Corp");

        Assert.Throws<DomainException>(() => tenant.SetMaxActiveLines(quota));
        Assert.Equal(0, tenant.MaxActiveLines);
    }
}
