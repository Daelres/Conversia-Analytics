using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>Contracts of <c>/api/branches</c>, <c>/api/advisors</c> and <c>/api/channels</c> for an owner of one tenant.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class OrganizationEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OrganizationEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<TestTenant> NewTenantAsync() => StartTenantAsync(_factory, _client, "org");

    // ---- Branches ------------------------------------------------------------------------

    [Fact]
    public async Task CreateBranch_Returns201WithLocation_AndTheBranchIsListedAndReadable()
    {
        using var tenant = await NewTenantAsync();

        var response = await PostBranchAsync(tenant.Owner.Client, "  Centro  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var branch = await ReadBodyAsync<BranchResponse>(response);
        Assert.Equal("Centro", branch.Name);
        Assert.True(branch.IsActive);
        Assert.EndsWith($"{BranchesUrl}/{branch.Id}", response.Headers.Location!.ToString());
        Assert.Equal(branch.Id, Assert.Single((await tenant.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!).Id);
        Assert.Equal("Centro", (await tenant.Owner.Client.GetFromJsonAsync<BranchResponse>($"{BranchesUrl}/{branch.Id}"))!.Name);
    }

    [Fact]
    public async Task ListBranches_IsOrderedByName()
    {
        using var tenant = await NewTenantAsync();
        foreach (var name in new[] { "Sur", "Centro", "Norte" })
        {
            await CreateBranchAsync(tenant.Owner.Client, name);
        }

        var branches = await tenant.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl);

        Assert.Equal(["Centro", "Norte", "Sur"], branches!.Select(b => b.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateBranch_WithoutAName_Is400WithAFieldErrorAndStoresNothing(string? name)
    {
        using var tenant = await NewTenantAsync();

        var response = await PostBranchAsync(tenant.Owner.Client, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Name", (await ReadProblemJsonAsync(response)).Extensions["errors"]!.ToString()!);
        Assert.Empty((await tenant.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!);
    }

    [Fact]
    public async Task CreateBranch_WithANulCharacterOrAnOverlongName_Is400()
    {
        using var tenant = await NewTenantAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostBranchAsync(tenant.Owner.Client, "Cen\0tro")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBranchAsync(tenant.Owner.Client, new string('a', Branch.NameMaxLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostBranchAsync(tenant.Owner.Client, new string('a', Branch.NameMaxLength))).StatusCode);
    }

    [Fact]
    public async Task CreateBranch_WithTheSameNameIgnoringCase_Is409()
    {
        using var tenant = await NewTenantAsync();
        await CreateBranchAsync(tenant.Owner.Client, "Centro");

        var response = await PostBranchAsync(tenant.Owner.Client, "CENTRO");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Ya existe una sucursal con ese nombre.", (await ReadProblemJsonAsync(response)).Detail);
        Assert.Single((await tenant.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!);
    }

    [Fact]
    public async Task CreateBranch_ConcurrentSameName_ExactlyOneWins()
    {
        using var tenant = await NewTenantAsync();
        var name = UniqueName("Race");

        var statuses = await RunInParallelAsync(Enumerable.Range(0, 8).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => PostBranchAsync(tenant.Owner.Client, name)));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task GetBranch_WithAnUnknownId_Is404()
    {
        using var tenant = await NewTenantAsync();

        var response = await tenant.Owner.Client.GetAsync($"{BranchesUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Sucursal no encontrada.", (await ReadProblemJsonAsync(response)).Detail);
    }

    // ---- Advisors ------------------------------------------------------------------------

    [Fact]
    public async Task CreateAdvisor_LinksTheBranchAndOptionallyAUserOfTheTenant()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var plain = await CreateAdvisorAsync(tenant.Owner.Client, branch.Id, "Ana");
        var linked = await CreateAdvisorAsync(tenant.Owner.Client, branch.Id, "Beto", tenant.Session.OwnerId);

        Assert.Equal(branch.Id, plain.BranchId);
        Assert.Null(plain.UserId);
        Assert.Equal(tenant.Session.OwnerId, linked.UserId);
        Assert.Equal(["Ana", "Beto"], (await tenant.Owner.Client.GetFromJsonAsync<List<AdvisorResponse>>(AdvisorsUrl))!.Select(a => a.Name));
        Assert.Equal(plain.Id, (await tenant.Owner.Client.GetFromJsonAsync<AdvisorResponse>($"{AdvisorsUrl}/{plain.Id}"))!.Id);
    }

    [Fact]
    public async Task CreateAdvisor_WithUnknownBranchOrUser_Is400AndStoresNothing()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var noBranch = await PostAdvisorAsync(tenant.Owner.Client, "Ana", Guid.NewGuid());
        var noUser = await PostAdvisorAsync(tenant.Owner.Client, "Ana", branch.Id, Guid.NewGuid());
        var missingBranch = await PostAdvisorAsync(tenant.Owner.Client, "Ana", null);
        var noName = await PostAdvisorAsync(tenant.Owner.Client, " ", branch.Id);

        Assert.All(new[] { noBranch, noUser, missingBranch, noName }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal("La sucursal indicada no existe.", (await ReadProblemJsonAsync(noBranch)).Detail);
        Assert.Equal("El usuario indicado no existe.", (await ReadProblemJsonAsync(noUser)).Detail);
        Assert.Empty((await tenant.Owner.Client.GetFromJsonAsync<List<AdvisorResponse>>(AdvisorsUrl))!);
    }

    [Fact]
    public async Task GetAdvisor_WithAnUnknownId_Is404()
        => Assert.Equal(HttpStatusCode.NotFound, (await (await NewOwnerClientAsync()).GetAsync($"{AdvisorsUrl}/{Guid.NewGuid()}")).StatusCode);

    // ---- Channels ------------------------------------------------------------------------

    [Fact]
    public async Task CreateChannel_ReturnsTheWebhookPathAndTheSecretOnce_AndNeverTheHash()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var response = await PostChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 123 4567", "1234567890");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var raw = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<CreateChannelResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(created.ChannelKey!.Length >= 32);
        Assert.Equal($"/api/webhooks/360dialog/{created.ChannelKey}", created.WebhookPath);
        Assert.True(created.WebhookSecret.Length >= 32);
        Assert.True(created.IsActive);
        Assert.Equal("1234567890", created.ProviderPhoneNumberId);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);

        // Later reads never repeat the secret.
        var listedRaw = await tenant.Owner.Client.GetStringAsync(ChannelsUrl);
        var byIdRaw = await tenant.Owner.Client.GetStringAsync($"{ChannelsUrl}/{created.Id}");
        Assert.DoesNotContain(created.WebhookSecret, listedRaw);
        Assert.DoesNotContain(created.WebhookSecret, byIdRaw);
        Assert.DoesNotContain("secret", byIdRaw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.ChannelKey, listedRaw);
    }

    [Fact]
    public async Task CreateChannel_StoresOnlyTheHashOfTheSecret()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var created = await CreateChannelAsync(tenant.Owner.Client, branch.Id);

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal(tenant.TenantId, stored.TenantId);
        Assert.Equal(WhatsAppChannel.HashSecret(created.WebhookSecret), stored.WebhookSecretHash);
        Assert.True(stored.VerifyWebhookSecret(created.WebhookSecret));
    }

    [Fact]
    public async Task CreateChannel_TwoChannels_GetDifferentKeysAndSecrets()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var first = await CreateChannelAsync(tenant.Owner.Client, branch.Id);
        var second = await CreateChannelAsync(tenant.Owner.Client, branch.Id, "3109876543");

        Assert.NotEqual(first.ChannelKey, second.ChannelKey);
        Assert.NotEqual(first.WebhookSecret, second.WebhookSecret);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not a phone")]
    [InlineData("")]
    public async Task CreateChannel_WithAnInvalidPhone_Is400(string phone)
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostChannelAsync(tenant.Owner.Client, branch.Id, phone)).StatusCode);
        Assert.Empty((await tenant.Owner.Client.GetFromJsonAsync<List<ChannelResponse>>(ChannelsUrl))!);
    }

    [Fact]
    public async Task CreateChannel_WithUnknownOrMissingBranchOrABadProviderId_Is400()
    {
        using var tenant = await NewTenantAsync();
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostChannelAsync(tenant.Owner.Client, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostChannelAsync(tenant.Owner.Client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostChannelAsync(tenant.Owner.Client, branch.Id, "3001234567", "two words")).StatusCode);
    }

    [Fact]
    public async Task GetChannel_WithAnUnknownId_Is404()
        => Assert.Equal(HttpStatusCode.NotFound, (await (await NewOwnerClientAsync()).GetAsync($"{ChannelsUrl}/{Guid.NewGuid()}")).StatusCode);

    [Fact]
    public async Task ErrorMessages_AreSpanish()
    {
        using var tenant = await NewTenantAsync();

        var problem = await ReadProblemJsonAsync(await PostBranchAsync(tenant.Owner.Client, ""));

        Assert.Contains("obligatorio", problem.Extensions["errors"]!.ToString()!);
    }

    private async Task<HttpClient> NewOwnerClientAsync()
    {
        var tenant = await NewTenantAsync();
        return tenant.Owner.Client;
    }
}
