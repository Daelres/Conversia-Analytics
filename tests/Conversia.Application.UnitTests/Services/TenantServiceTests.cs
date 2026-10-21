using Conversia.Application.Exceptions;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class TenantServiceTests
{
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();

    private TenantService CreateSut() => new(_tenants.Object, _channels.Object);

    [Fact]
    public async Task GetCurrent_ReturnsTheStatusTheQuotaAndTheLineUsage()
    {
        var tenant = Tenant.Create("Acme");
        tenant.SetMaxActiveLines(4);
        _tenants.Setup(r => r.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _channels.Setup(r => r.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Registered: 5, Active: 2));

        var info = await CreateSut().GetCurrentAsync(tenant.Id);

        Assert.Equal((tenant.Id, "Acme", TenantStatus.Pending, 4, 2, 5), (info.Id, info.Name, info.Status, info.MaxActiveLines, info.ActiveLines, info.RegisteredLines));
    }

    [Fact]
    public async Task GetCurrent_ForAMissingTenant_ThrowsNotFound()
        => await Assert.ThrowsAsync<TenantNotFoundException>(() => CreateSut().GetCurrentAsync(Guid.NewGuid()));
}
