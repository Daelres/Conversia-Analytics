using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class UserTests
{
    private static readonly Guid ValidTenantId = Guid.NewGuid();
    private const string ValidEmail = "owner@acme.test";
    private const string ValidPasswordHash = "AQAAAAIAAYagAAAAEDummyHashValue==";

    [Fact]
    public void Create_WithValidInput_ReturnsUserWithNonEmptyIdAndGivenValues()
    {
        var user = User.Create(ValidTenantId, ValidEmail, ValidPasswordHash, isOwner: true);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(ValidTenantId, user.TenantId);
        Assert.Equal(ValidEmail, user.Email);
        Assert.Equal(ValidPasswordHash, user.PasswordHash);
        Assert.True(user.IsOwner);
    }

    [Fact]
    public void Create_WithANulCharacterInTheEmail_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => User.Create(ValidTenantId, "own\0er@acme.test", ValidPasswordHash, isOwner: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyOrWhitespaceEmail_ThrowsDomainException(string? email)
    {
        Assert.Throws<DomainException>(() => User.Create(ValidTenantId, email!, ValidPasswordHash, isOwner: false));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.test")]
    [InlineData("@no-local-part.test")]
    [InlineData("no-domain@")]
    public void Create_WithMalformedEmail_ThrowsDomainException(string malformedEmail)
    {
        Assert.Throws<DomainException>(() => User.Create(ValidTenantId, malformedEmail, ValidPasswordHash, isOwner: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithEmptyOrWhitespacePasswordHash_ThrowsDomainException(string? passwordHash)
    {
        Assert.Throws<DomainException>(() => User.Create(ValidTenantId, ValidEmail, passwordHash!, isOwner: false));
    }

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => User.Create(Guid.Empty, ValidEmail, ValidPasswordHash, isOwner: false));
    }
}
