using System.Security.Claims;
using Conversia.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Conversia.Infrastructure.UnitTests.Services;

/// <summary>Where the tenant of a scope comes from: the JWT claim of a request, or the one a webhook call or the worker sets explicitly.</summary>
public class CurrentTenantServiceTests
{
    private static CurrentTenantService NewService(Guid? claimTenant)
    {
        var context = new DefaultHttpContext();
        if (claimTenant is { } tenant)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenant.ToString())], "TestAuth"));
        }

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        return new CurrentTenantService(accessor.Object);
    }

    [Fact]
    public void WithoutAClaimOrAnExplicitTenant_NoTenantIsVisible()
        => Assert.Equal(Guid.Empty, NewService(null).TenantId);

    [Fact]
    public void TheClaimIsUsedWhenNothingWasSetExplicitly()
    {
        var tenant = Guid.NewGuid();

        Assert.Equal(tenant, NewService(tenant).TenantId);
    }

    [Fact]
    public void AnExplicitTenant_IsUsedWhereThereIsNoRequestOrNoClaim()
    {
        var tenant = Guid.NewGuid();
        var service = new CurrentTenantService(Mock.Of<IHttpContextAccessor>());

        service.SetTenant(tenant);

        Assert.Equal(tenant, service.TenantId);
    }

    [Fact]
    public void AnExplicitTenant_WinsOverTheClaim()
    {
        var explicitTenant = Guid.NewGuid();
        var service = NewService(Guid.NewGuid());

        service.SetTenant(explicitTenant);

        Assert.Equal(explicitTenant, service.TenantId);
    }

    [Fact]
    public void AnEmptyTenant_IsRefused_AndLeavesTheCurrentOneAlone()
    {
        var tenant = Guid.NewGuid();
        var service = NewService(tenant);

        Assert.Throws<ArgumentException>(() => service.SetTenant(Guid.Empty));

        Assert.Equal(tenant, service.TenantId);
    }

    [Fact]
    public void APlatformAdministratorsPrincipal_HasNoTenant_SoNoCompanyDataIsVisibleToIt()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "platform_admin")], "TestAuth")),
        };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);

        Assert.Equal(Guid.Empty, new CurrentTenantService(accessor.Object).TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ABadTenantClaim_IsNoTenant(string claim)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", claim)], "TestAuth")) };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);

        Assert.Equal(Guid.Empty, new CurrentTenantService(accessor.Object).TenantId);
    }
}
