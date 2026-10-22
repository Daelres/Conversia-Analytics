using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>What <see cref="OrganizationService"/> returns, throws and hands to the repositories. Tenant isolation is the query filter's job (integration tests).</summary>
public class OrganizationServiceTests
{
    private readonly Mock<IBranchRepository> _branches = new();
    private readonly Mock<IAdvisorRepository> _advisors = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private OrganizationService CreateSut() => new(_branches.Object, _advisors.Object, _channels.Object, _users.Object, new Mock<ITenantUnitOfWork>().Object);

    private Branch StoredBranch()
    {
        var branch = Branch.Create(_tenant, "Centro");
        _branches.Setup(r => r.GetByIdAsync(branch.Id, It.IsAny<CancellationToken>())).ReturnsAsync(branch);
        return branch;
    }

    [Fact]
    public async Task CreateBranch_PersistsTheBranchInTheCallersTenantAndReturnsIt()
    {
        Branch? saved = null;
        _branches.Setup(r => r.AddAsync(It.IsAny<Branch>(), It.IsAny<CancellationToken>())).Callback<Branch, CancellationToken>((b, _) => saved = b);

        var response = await CreateSut().CreateBranchAsync(_tenant, new CreateBranchRequest { Name = " Centro " });

        Assert.Equal(_tenant, saved!.TenantId);
        Assert.Equal("Centro", response.Name);
        Assert.Equal(saved.Id, response.Id);
    }

    [Fact]
    public async Task CreateBranch_LetsTheDuplicateNameExceptionOfTheRepositoryThrough()
    {
        _branches.Setup(r => r.AddAsync(It.IsAny<Branch>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DuplicateBranchNameException());

        await Assert.ThrowsAsync<DuplicateBranchNameException>(() => CreateSut().CreateBranchAsync(_tenant, new CreateBranchRequest { Name = "Centro" }));
    }

    [Fact]
    public async Task CreateBranch_WithAnInvalidNameThrowsADomainExceptionAndStoresNothing()
    {
        await Assert.ThrowsAsync<DomainException>(() => CreateSut().CreateBranchAsync(_tenant, new CreateBranchRequest { Name = " " }));

        _branches.Verify(r => r.AddAsync(It.IsAny<Branch>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBranch_ThrowsNotFoundWhenTheRepositoryReturnsNull()
        => await Assert.ThrowsAsync<BranchNotFoundException>(() => CreateSut().GetBranchAsync(Guid.NewGuid()));

    [Fact]
    public async Task CreateAdvisor_RequiresAnExistingBranch()
    {
        var request = new CreateAdvisorRequest { Name = "Ana", BranchId = Guid.NewGuid() };

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().CreateAdvisorAsync(_tenant, request));

        _advisors.Verify(r => r.AddAsync(It.IsAny<Advisor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAdvisor_WithALinkedUserChecksItExists()
    {
        var branch = StoredBranch();
        var request = new CreateAdvisorRequest { Name = "Ana", BranchId = branch.Id, UserId = Guid.NewGuid() };

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().CreateAdvisorAsync(_tenant, request));

        _users.Setup(r => r.GetByIdAsync(request.UserId!.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User.Create(_tenant, "ana@example.test", "hash", isOwner: false));
        var response = await CreateSut().CreateAdvisorAsync(_tenant, request);
        Assert.Equal(request.UserId, response.UserId);
        Assert.Equal(branch.Id, response.BranchId);
    }

    [Fact]
    public async Task CreateChannel_ReturnsTheSecretOnceAndStoresOnlyItsHash()
    {
        var branch = StoredBranch();
        WhatsAppChannel? saved = null;
        _channels.Setup(r => r.AddAsync(It.IsAny<WhatsAppChannel>(), It.IsAny<CancellationToken>())).Callback<WhatsAppChannel, CancellationToken>((c, _) => saved = c);

        var response = await CreateSut().CreateChannelAsync(_tenant, new CreateChannelRequest { BranchId = branch.Id, DisplayPhoneNumber = "3001234567" });

        Assert.True(saved!.VerifyWebhookSecret(response.WebhookSecret));
        Assert.Equal(saved.ChannelKey, response.ChannelKey);
        Assert.Equal($"/api/webhooks/360dialog/{saved.ChannelKey}", response.WebhookPath);
        Assert.Equal(_tenant, saved.TenantId);
    }

    [Fact]
    public async Task CreateChannel_RequiresAnExistingBranch()
    {
        await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().CreateChannelAsync(_tenant, new CreateChannelRequest { BranchId = Guid.NewGuid(), DisplayPhoneNumber = "3001234567" }));

        _channels.Verify(r => r.AddAsync(It.IsAny<WhatsAppChannel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task List_MapsWhatTheRepositoryReturns()
    {
        _branches.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Branch.Create(_tenant, "A"), Branch.Create(_tenant, "B")]);

        Assert.Equal(["A", "B"], (await CreateSut().ListBranchesAsync()).Select(b => b.Name));
    }
}
