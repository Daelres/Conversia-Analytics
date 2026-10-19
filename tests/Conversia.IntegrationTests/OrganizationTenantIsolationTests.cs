using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Organization;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Branches, advisors and channels live in the tenant of the caller's JWT only. Each refusal for tenant B is checked against
/// tenant A's own successful access to the same id, so a missing route cannot satisfy it.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class OrganizationTenantIsolationTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OrganizationTenantIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AnotherTenantsRecords_AreInvisibleByListAndByIdWithTheSame404AsAnUnknownId()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");
        var branch = await CreateBranchAsync(a.Owner.Client);
        var advisor = await CreateAdvisorAsync(a.Owner.Client, branch.Id);
        var channel = await CreateChannelAsync(a.Owner.Client, branch.Id);

        foreach (var url in new[] { $"{BranchesUrl}/{branch.Id}", $"{AdvisorsUrl}/{advisor.Id}", $"{ChannelsUrl}/{channel.Id}" })
        {
            Assert.Equal(HttpStatusCode.OK, (await a.Owner.Client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.Client.GetAsync(url)).StatusCode);
        }

        Assert.Empty((await b.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!);
        Assert.Empty((await b.Owner.Client.GetFromJsonAsync<List<AdvisorResponse>>(AdvisorsUrl))!);
        Assert.Empty((await b.Owner.Client.GetFromJsonAsync<List<ChannelResponse>>(ChannelsUrl))!);
    }

    [Fact]
    public async Task TheSameBranchNameCanExistInTwoTenants()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");
        var name = UniqueName("Centro");

        await CreateBranchAsync(a.Owner.Client, name);
        await CreateBranchAsync(b.Owner.Client, name);

        Assert.Equal(HttpStatusCode.Conflict, (await PostBranchAsync(a.Owner.Client, name)).StatusCode);
    }

    [Fact]
    public async Task ABranchOrUserOfAnotherTenant_CannotBeReferenced()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");
        var aBranch = await CreateBranchAsync(a.Owner.Client);
        var bBranch = await CreateBranchAsync(b.Owner.Client);

        // The control: each tenant can use its own branch and user.
        await CreateAdvisorAsync(b.Owner.Client, bBranch.Id, userId: b.Session.OwnerId);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAdvisorAsync(b.Owner.Client, "Intruso", aBranch.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostChannelAsync(b.Owner.Client, aBranch.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAdvisorAsync(b.Owner.Client, "Intruso", bBranch.Id, a.Session.OwnerId)).StatusCode);
        Assert.Single((await b.Owner.Client.GetFromJsonAsync<List<AdvisorResponse>>(AdvisorsUrl))!);
        Assert.Empty((await a.Owner.Client.GetFromJsonAsync<List<AdvisorResponse>>(AdvisorsUrl))!);
        Assert.Empty((await a.Owner.Client.GetFromJsonAsync<List<ChannelResponse>>(ChannelsUrl))!);
    }

    [Fact]
    public async Task ATenantIdInTheBodyIsIgnored_TheRecordLandsInTheCallersTenant()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");

        var response = await a.Owner.Client.PostAsJsonAsync(BranchesUrl, new { name = UniqueName("Mine"), tenantId = b.TenantId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single((await a.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!);
        Assert.Empty((await b.Owner.Client.GetFromJsonAsync<List<BranchResponse>>(BranchesUrl))!);
    }
}
