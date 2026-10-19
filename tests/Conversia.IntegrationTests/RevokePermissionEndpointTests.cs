using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>DELETE /api/users/{userId}/permissions/{permission}</c>: revokes a
/// permission from a user of the caller's own tenant, effective on the
/// target's very next request. Targets are seeded through the repository and
/// permissions granted through the existing assignment endpoint, so these
/// tests depend on nothing but the endpoint under test.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class RevokePermissionEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RevokePermissionEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record Scenario(TenantSession Owner, HttpClient OwnerClient, MemberSession Member) : IDisposable
    {
        public void Dispose() => OwnerClient.Dispose();
    }

    private async Task<Scenario> StartScenarioAsync(params Permission[] memberPermissions)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var member = await SeedMemberAsync(_factory, _client, owner.TenantId);
        foreach (var permission in memberPermissions)
        {
            await GrantAsync(ownerClient, member.UserId, permission);
        }

        return new Scenario(owner, ownerClient, member);
    }

    private async Task<UserProfileResponse> GetProfileAsync(string token)
    {
        using var client = AuthorizedClient(_factory, token);
        var response = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserProfileResponse>())!;
    }

    // ---- Happy path ---------------------------------------------------------------

    [Fact]
    public async Task Revoke_ByOwner_Returns204AndTheTargetsNextRequestWithTheSameTokenIsForbidden()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, s.Member.Token));

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, s.Member.Token)); // same token, no re-login
        Assert.Empty(await PersistedPermissionsAsync(_factory, s.Member.UserId));
        Assert.Empty((await GetProfileAsync(s.Member.Token)).Permissions);
    }

    [Fact]
    public async Task Revoke_RemovesOnlyTheNamedPermission()
    {
        using var s = await StartScenarioAsync(Permission.UsersView, Permission.UsersManage);

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = Assert.Single(await PersistedPermissionsAsync(_factory, s.Member.UserId));
        Assert.Equal(Permission.UsersManage, remaining);
        Assert.Equal(new[] { Permission.UsersManage }, (await GetProfileAsync(s.Member.Token)).Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, s.Member.Token));
    }

    [Fact]
    public async Task Revoke_DoesNotAffectTheSamePermissionHeldByOtherUsersInThisOrAnotherTenant()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        var sameTenantPeer = await SeedMemberAsync(_factory, _client, s.Owner.TenantId, "peer");
        await GrantAsync(s.OwnerClient, sameTenantPeer.UserId, Permission.UsersView);
        var otherOwner = await RegisterTenantSessionAsync(_client, "other");
        using var otherOwnerClient = AuthorizedClient(_factory, otherOwner.OwnerToken);
        var otherTenantMember = await SeedMemberAsync(_factory, _client, otherOwner.TenantId, "outsider");
        await GrantAsync(otherOwnerClient, otherTenantMember.UserId, Permission.UsersView);

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, s.Member.Token));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, sameTenantPeer.Token));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, otherTenantMember.Token));
    }

    [Fact]
    public async Task Revoke_ThenGrantAgain_RestoresAccess()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, s.Member.Token));

        await GrantAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, s.Member.Token));
        Assert.Single(await PersistedPermissionsAsync(_factory, s.Member.UserId));
    }

    [Fact]
    public async Task Revoke_ByNonOwnerHoldingUsersManage_Returns204()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        var manager = await SeedMemberAsync(_factory, _client, s.Owner.TenantId, "manager");
        await GrantAsync(s.OwnerClient, manager.UserId, Permission.UsersManage);
        using var managerClient = AuthorizedClient(_factory, manager.Token);

        var response = await RevokeAsync(managerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, s.Member.Token));
    }

    [Fact]
    public async Task Revoke_OfTheCallersOwnUsersManage_Returns204AndTakesEffectImmediately()
    {
        using var s = await StartScenarioAsync(Permission.UsersManage);
        using var memberClient = AuthorizedClient(_factory, s.Member.Token);

        var response = await RevokeAsync(memberClient, s.Member.UserId, Permission.UsersManage);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var second = await RevokeAsync(memberClient, s.Member.UserId, Permission.UsersManage);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task Revoke_OfAnOwnersExplicitRow_Returns204AndTheOwnerKeepsFullAccessThroughTheOwnerBypass()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        await GrantAsync(ownerClient, owner.OwnerId, Permission.UsersView);

        var response = await RevokeAsync(ownerClient, owner.OwnerId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await PersistedPermissionsAsync(_factory, owner.OwnerId));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, owner.OwnerToken));
    }

    // ---- Idempotency ---------------------------------------------------------------

    [Fact]
    public async Task Revoke_OfAPermissionTheTargetDoesNotHold_Returns204AndLeavesOtherPermissionsAlone()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersManage);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, s.Member.UserId));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, s.Member.Token));
    }

    [Fact]
    public async Task Revoke_RepeatedForTheSamePermission_Returns204EveryTime()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);

        var first = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);
        var second = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);
        var third = await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, third.StatusCode);
    }

    // ---- Permission route value -----------------------------------------------------

    public static TheoryData<string, Permission> AcceptedPermissionSpellings => new()
    {
        { "UsersView", Permission.UsersView },
        { "usersview", Permission.UsersView },
        { "USERSVIEW", Permission.UsersView },
        { "1", Permission.UsersView },
        { "UsersManage", Permission.UsersManage },
        { "usersMANAGE", Permission.UsersManage },
        { "2", Permission.UsersManage },
    };

    [Theory]
    [MemberData(nameof(AcceptedPermissionSpellings))]
    public async Task Revoke_AcceptsPermissionByNameCaseInsensitiveOrByNumber(string spelling, Permission held)
    {
        using var s = await StartScenarioAsync(held);

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, spelling);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await PersistedPermissionsAsync(_factory, s.Member.UserId));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("99")]                      // one past the catalog
    [InlineData("NotAPermission")]
    // Enum parsing accepts comma lists and ORs their members, so a lazy "result is undefined" check misses the ones that collapse to a defined value.
    [InlineData("UsersView,UsersManage")]   // 1|2 = 3: a retired number, not a defined permission
    [InlineData("UsersView,UsersView")]     // 1|1 = 1 = UsersView: collapses to the very permission the caller holds
    [InlineData("UsersManage,UsersManage")] // 2|2 = 2 = UsersManage
    [InlineData("1,2")]                     // regression guard: Enum.TryParse already rejects a numeric comma list today
    [InlineData("UsersView,OrganizationManage")] // 1|6 = 7 = ConversationsView: collapses to a defined value
    [InlineData("UsersManage,OrganizationView")] // 2|5 = 7 = ConversationsView: collapses to a defined value
    [InlineData("99999999999")]             // overflows Int32
    [InlineData("1.5")]
    public async Task Revoke_WithUndefinedPermission_Returns400AndChangesNothing(string permission)
    {
        using var s = await StartScenarioAsync(Permission.UsersView, Permission.UsersManage);

        var response = await RevokeAsync(s.OwnerClient, s.Member.UserId, permission);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var remaining = await PersistedPermissionsAsync(_factory, s.Member.UserId);
        Assert.Equal(
            new[] { Permission.UsersView, Permission.UsersManage }.Order(),
            remaining.Order());
    }

    // ---- Target resolution / tenant isolation ----------------------------------------

    [Fact]
    public async Task Revoke_WithNonGuidUserId_Returns404AndChangesNothing()
    {
        using var s = await StartScenarioAsync(Permission.UsersView, Permission.UsersManage);
        // Prove the route is live first, so the 404 below is the :guid constraint and not a missing endpoint.
        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(s.OwnerClient, s.Member.UserId, Permission.UsersManage)).StatusCode);

        var responses = new[]
        {
            await s.OwnerClient.DeleteAsync("/api/users/not-a-guid/permissions/UsersView"),
            await s.OwnerClient.DeleteAsync("/api/users/12345/permissions/UsersView"),
            await s.OwnerClient.DeleteAsync($"/api/users/{s.Member.UserId}x/permissions/UsersView"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, s.Member.UserId));
    }

    [Fact]
    public async Task Revoke_ForNonexistentAndForCrossTenantTargets_ReturnIndistinguishable404ProblemJson()
    {
        using var s = await StartScenarioAsync();
        var otherOwner = await RegisterTenantSessionAsync(_client, "other");
        using var otherOwnerClient = AuthorizedClient(_factory, otherOwner.OwnerToken);
        var otherMember = await SeedMemberAsync(_factory, _client, otherOwner.TenantId, "outsider");
        await GrantAsync(otherOwnerClient, otherMember.UserId, Permission.UsersView);
        await GrantAsync(otherOwnerClient, otherOwner.OwnerId, Permission.UsersView);

        var nonexistent = await RevokeAsync(s.OwnerClient, Guid.NewGuid(), Permission.UsersView);
        var emptyGuid = await RevokeAsync(s.OwnerClient, Guid.Empty, Permission.UsersView);
        var crossTenantMember = await RevokeAsync(s.OwnerClient, otherMember.UserId, Permission.UsersView);
        var crossTenantOwner = await RevokeAsync(s.OwnerClient, otherOwner.OwnerId, Permission.UsersView);

        var all = new[] { nonexistent, emptyGuid, crossTenantMember, crossTenantOwner };
        Assert.All(all, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var baseline = await ReadProblemJsonAsync(nonexistent);
        foreach (var response in all.Skip(1))
        {
            var problem = await ReadProblemJsonAsync(response);
            Assert.Equal(baseline.Status, problem.Status);
            Assert.Equal(baseline.Title, problem.Title);
            Assert.Equal(baseline.Detail, problem.Detail);
        }

        // The other tenant's rows are untouched and still effective.
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, otherMember.UserId));
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, otherOwner.OwnerId));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, otherMember.Token));
    }

    // ---- Authentication / authorization ------------------------------------------------

    [Fact]
    public async Task Revoke_WithoutToken_Returns401AndChangesNothing()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);

        var response = await RevokeAsync(_client, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, s.Member.UserId));
    }

    [Fact]
    public async Task Revoke_WithMalformedToken_Returns401AndChangesNothing()
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        using var client = AuthorizedClient(_factory, "not-a-real-jwt");

        var response = await RevokeAsync(client, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, s.Member.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Permission.UsersView)]
    public async Task Revoke_ByNonOwnerWithoutUsersManage_Returns403AndChangesNothing(Permission? callerPermission)
    {
        using var s = await StartScenarioAsync(Permission.UsersView);
        var caller = await SeedMemberAsync(_factory, _client, s.Owner.TenantId, "caller");
        if (callerPermission is { } held)
        {
            await GrantAsync(s.OwnerClient, caller.UserId, held);
        }

        using var callerClient = AuthorizedClient(_factory, caller.Token);

        var response = await RevokeAsync(callerClient, s.Member.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, s.Member.UserId));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, s.Member.Token));
    }

    [Fact]
    public async Task Revoke_ByNonOwnerWithoutUsersManageAgainstNonexistentOrCrossTenantTarget_Returns403NotTheTarget404()
    {
        // Authorization runs before target resolution: a caller lacking
        // UsersManage must not learn whether a target exists in any tenant.
        using var s = await StartScenarioAsync();
        var caller = await SeedMemberAsync(_factory, _client, s.Owner.TenantId, "caller");
        await GrantAsync(s.OwnerClient, caller.UserId, Permission.UsersView);
        var otherOwner = await RegisterTenantSessionAsync(_client, "other");
        using var otherOwnerClient = AuthorizedClient(_factory, otherOwner.OwnerToken);
        var otherMember = await SeedMemberAsync(_factory, _client, otherOwner.TenantId, "outsider");
        await GrantAsync(otherOwnerClient, otherMember.UserId, Permission.UsersView);
        using var callerClient = AuthorizedClient(_factory, caller.Token);

        using var nonexistent = await RevokeAsync(callerClient, Guid.NewGuid(), Permission.UsersView);
        using var crossTenant = await RevokeAsync(callerClient, otherMember.UserId, Permission.UsersView);

        Assert.Equal(HttpStatusCode.Forbidden, nonexistent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, otherMember.UserId));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, otherMember.Token));
    }
}
