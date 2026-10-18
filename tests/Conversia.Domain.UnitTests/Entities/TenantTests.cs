using Conversia.Domain.Entities;
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
}
