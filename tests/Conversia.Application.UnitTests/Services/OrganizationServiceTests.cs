using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
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

    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);
    private readonly ReversingProtector _protector = new();

    private OrganizationService CreateSut()
        => new(_branches.Object, _advisors.Object, _channels.Object, _users.Object, new Mock<ITenantUnitOfWork>().Object, _protector, new FixedTimeProvider(Now));

    /// <summary>Not encryption: enough to tell "the stored value is what the protector returned for this purpose" from "the plaintext was stored".</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public bool IsConfigured => true;

        public string Protect(string plaintext, string purpose) => $"protected[{purpose}]:{new string(plaintext.Reverse().ToArray())}";

        public string Unprotect(string protectedValue, string purpose) => throw new NotSupportedException();
    }

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

    // ---- Provider credentials ------------------------------------------------------------------

    private WhatsAppChannel StoredChannel(bool withKey = false)
    {
        var (channel, _) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "3001234567");
        if (withKey)
        {
            channel.SetProviderCredentials("protected:old", WhatsAppProviderEnvironment.Sandbox, Now.AddDays(-1));
        }

        _channels.Setup(r => r.GetForUpdateAsync(channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync(channel);
        return channel;
    }

    [Fact]
    public async Task SetProviderCredentials_StoresOnlyTheProtectedKey_BoundToTheChannel_AndAnswersWithoutIt()
    {
        var channel = StoredChannel();

        var response = await CreateSut().SetChannelProviderCredentialsAsync(
            channel.Id, new SetProviderCredentialsRequest { ApiKey = "  abc-123  ", Environment = "production" });

        Assert.Equal($"protected[{ProviderCredentialPurpose.ForChannel(channel.Id)}]:321-cba", channel.ProviderApiKeyCiphertext);
        Assert.Equal(WhatsAppProviderEnvironment.Production, channel.ProviderEnvironment);
        Assert.Equal(Now, channel.ProviderApiKeySetAt);
        _channels.Verify(r => r.UpdateAsync(channel, It.IsAny<CancellationToken>()), Times.Once);

        Assert.True(response.HasProviderApiKey);
        Assert.Equal(Now, response.ProviderApiKeySetAt);
        Assert.Equal(nameof(WhatsAppProviderEnvironment.Production), response.ProviderEnvironment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SetProviderCredentials_WithoutAKey_ChangesOnlyTheEnvironmentOfAChannelThatHasOne(string? apiKey)
    {
        var channel = StoredChannel(withKey: true);

        var response = await CreateSut().SetChannelProviderCredentialsAsync(
            channel.Id, new SetProviderCredentialsRequest { ApiKey = apiKey, Environment = "Production" });

        Assert.Equal("protected:old", channel.ProviderApiKeyCiphertext);
        Assert.Equal(Now.AddDays(-1), channel.ProviderApiKeySetAt);
        Assert.Equal(WhatsAppProviderEnvironment.Production, channel.ProviderEnvironment);
        Assert.True(response.HasProviderApiKey);
    }

    [Fact]
    public async Task SetProviderCredentials_WithoutAKey_OnAChannelThatHasNone_IsRefused()
    {
        var channel = StoredChannel();

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetChannelProviderCredentialsAsync(
            channel.Id, new SetProviderCredentialsRequest { Environment = "Sandbox" }));

        _channels.Verify(r => r.UpdateAsync(It.IsAny<WhatsAppChannel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("two words", "Sandbox")]
    [InlineData("abc-123", "Staging")]
    [InlineData("abc-123", null)]
    public async Task SetProviderCredentials_RefusesAnInvalidKeyOrEnvironment_AndStoresNothing(string apiKey, string? environment)
    {
        var channel = StoredChannel();

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetChannelProviderCredentialsAsync(
            channel.Id, new SetProviderCredentialsRequest { ApiKey = apiKey, Environment = environment }));

        Assert.False(channel.HasProviderApiKey);
        _channels.Verify(r => r.UpdateAsync(It.IsAny<WhatsAppChannel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProviderCredentials_OfAnUnknownChannel_AreNotFound()
    {
        await Assert.ThrowsAsync<ChannelNotFoundException>(() => CreateSut().SetChannelProviderCredentialsAsync(
            Guid.NewGuid(), new SetProviderCredentialsRequest { ApiKey = "abc-123", Environment = "Sandbox" }));
        await Assert.ThrowsAsync<ChannelNotFoundException>(() => CreateSut().ClearChannelProviderCredentialsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ClearProviderCredentials_ForgetsTheKey()
    {
        var channel = StoredChannel(withKey: true);

        var response = await CreateSut().ClearChannelProviderCredentialsAsync(channel.Id);

        Assert.False(channel.HasProviderApiKey);
        Assert.False(response.HasProviderApiKey);
        Assert.Null(response.ProviderApiKeySetAt);
        _channels.Verify(r => r.UpdateAsync(channel, It.IsAny<CancellationToken>()), Times.Once);
    }
}
