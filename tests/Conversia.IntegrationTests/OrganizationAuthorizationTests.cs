using System.Net;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Who may do what on branches, advisors and channels: <c>OrganizationView</c> reads, <c>OrganizationManage</c> writes, the owner
/// bypasses, no token is 401. Every refusal is paired with the success of someone allowed on the same request.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class OrganizationAuthorizationTests
{
    public enum Op { ListBranches, GetBranch, CreateBranch, ListAdvisors, GetAdvisor, CreateAdvisor, ListChannels, GetChannel, CreateChannel, DeactivateChannel, ActivateChannel, RotateSecret }

    private static readonly Op[] Reads = [Op.ListBranches, Op.GetBranch, Op.ListAdvisors, Op.GetAdvisor, Op.ListChannels, Op.GetChannel];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OrganizationAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record Targets(Guid BranchId, Guid AdvisorId, Guid ChannelId);

    private static async Task<Targets> SeedAsync(HttpClient owner)
    {
        var branch = await CreateBranchAsync(owner);
        return new Targets(branch.Id, (await CreateAdvisorAsync(owner, branch.Id)).Id, (await CreateChannelAsync(owner, branch.Id)).Id);
    }

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, Op op, Targets t) => op switch
    {
        Op.ListBranches => client.GetAsync(BranchesUrl),
        Op.GetBranch => client.GetAsync($"{BranchesUrl}/{t.BranchId}"),
        Op.CreateBranch => PostBranchAsync(client, UniqueName("Nueva")),
        Op.ListAdvisors => client.GetAsync(AdvisorsUrl),
        Op.GetAdvisor => client.GetAsync($"{AdvisorsUrl}/{t.AdvisorId}"),
        Op.CreateAdvisor => PostAdvisorAsync(client, UniqueName("Nuevo"), t.BranchId),
        Op.ListChannels => client.GetAsync(ChannelsUrl),
        Op.GetChannel => client.GetAsync($"{ChannelsUrl}/{t.ChannelId}"),
        Op.CreateChannel => PostChannelAsync(client, t.BranchId),
        Op.DeactivateChannel => client.PostAsync($"{ChannelsUrl}/{t.ChannelId}/deactivate", null),
        Op.ActivateChannel => client.PostAsync($"{ChannelsUrl}/{t.ChannelId}/activate", null),
        _ => client.PostAsync($"{ChannelsUrl}/{t.ChannelId}/rotate-secret", null),
    };

    private static HttpStatusCode SuccessFor(Op op) => op is Op.CreateBranch or Op.CreateAdvisor or Op.CreateChannel ? HttpStatusCode.Created : HttpStatusCode.OK;

    public static TheoryData<Op> AllOps => new(Enum.GetValues<Op>());

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Anonymous_Is401_WhileTheOwnerSucceeds(Op op)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "auth");
        var targets = await SeedAsync(tenant.Owner.Client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RunAsync(_factory.CreateClient(), op, targets)).StatusCode);
        Assert.Equal(SuccessFor(op), (await RunAsync(tenant.Owner.Client, op, targets)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task MemberWithoutOrganizationPermissions_Is403_AndAUsersOrProductsPermissionDoesNotHelp(Op op)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "auth");
        var targets = await SeedAsync(tenant.Owner.Client);
        using var none = await tenant.AddMemberAsync("none");
        using var others = await tenant.AddMemberAsync("others", Permission.UsersView, Permission.UsersManage, Permission.ProductsView, Permission.ProductsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(none.Client, op, targets)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(others.Client, op, targets)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task OrganizationView_AllowsExactlyTheReads(Op op)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "auth");
        var targets = await SeedAsync(tenant.Owner.Client);
        using var viewer = await tenant.AddMemberAsync("viewer", Permission.OrganizationView);

        var expected = Reads.Contains(op) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await RunAsync(viewer.Client, op, targets)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task OrganizationManage_AllowsExactlyTheWrites(Op op)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "auth");
        var targets = await SeedAsync(tenant.Owner.Client);
        using var manager = await tenant.AddMemberAsync("manager", Permission.OrganizationManage);

        var expected = Reads.Contains(op) ? HttpStatusCode.Forbidden : SuccessFor(op);
        Assert.Equal(expected, (await RunAsync(manager.Client, op, targets)).StatusCode);
    }

    [Fact]
    public async Task ARevokedPermissionStopsWorkingOnTheNextRequest()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "auth");
        using var viewer = await tenant.AddMemberAsync("viewer", Permission.OrganizationView);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(BranchesUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(tenant.Owner.Client, viewer.UserId, Permission.OrganizationView)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync(BranchesUrl)).StatusCode);
    }
}
