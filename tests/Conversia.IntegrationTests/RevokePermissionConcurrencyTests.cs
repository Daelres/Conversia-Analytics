using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Concurrency/multi-user behaviour of permission revocation against the real
/// PostgreSQL: revokes race with each other, with assignments and with the
/// target's own requests, and must never surface a 5xx or leave the three
/// views of the state (persisted rows, <c>GET /api/users/me</c>, and the
/// <c>HasPermission</c> authorization decision) disagreeing.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class RevokePermissionConcurrencyTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RevokePermissionConcurrencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Func<Task<HttpResponseMessage>> Revoke(string token, Guid targetId, Permission permission) => async () =>
    {
        using var client = AuthorizedClient(_factory, token);
        return await RevokeAsync(client, targetId, permission);
    };

    private Func<Task<HttpResponseMessage>> Assign(string token, Guid targetId, Permission permission) => async () =>
    {
        using var client = AuthorizedClient(_factory, token);
        return await client.PostAsJsonAsync($"/api/users/{targetId}/permissions", new { permission = (int)permission });
    };

    private Func<Task<HttpResponseMessage>> ProbeUsersView(string token) => async () =>
    {
        using var client = AuthorizedClient(_factory, token);
        return await client.GetAsync("/api/users");
    };

    private static void AssertAll(HttpStatusCode?[] responses, HttpStatusCode expected)
    {
        Assert.All(responses, r => Assert.NotNull(r));
        Assert.All(responses, r => Assert.Equal(expected, r));
    }

    private async Task<(TenantSession Owner, MemberSession Member)> OwnerWithMemberAsync(params Permission[] granted)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var member = await SeedMemberAsync(_factory, _client, owner.TenantId);
        foreach (var permission in granted)
        {
            await GrantAsync(ownerClient, member.UserId, permission);
        }

        return (owner, member);
    }

    [Fact]
    public async Task Revoke_20ParallelIdenticalRequests_AllReturn204AndThePermissionIsGone()
    {
        var (owner, member) = await OwnerWithMemberAsync(Permission.UsersView);

        var responses = await RunInParallelAsync(
            Enumerable.Range(0, 20).Select(_ => Revoke(owner.OwnerToken, member.UserId, Permission.UsersView)));

        AssertAll(responses, HttpStatusCode.NoContent);
        Assert.Empty(await PersistedPermissionsAsync(_factory, member.UserId));
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, member.Token));
    }

    [Fact]
    public async Task Revoke_ParallelRequestsForDifferentPermissionsOfTheSameUser_AllReturn204AndBothAreGone()
    {
        var (owner, member) = await OwnerWithMemberAsync(Permission.UsersView, Permission.UsersManage);

        var responses = await RunInParallelAsync(Enumerable.Range(0, 10).SelectMany(_ => new[]
        {
            Revoke(owner.OwnerToken, member.UserId, Permission.UsersView),
            Revoke(owner.OwnerToken, member.UserId, Permission.UsersManage),
        }));

        AssertAll(responses, HttpStatusCode.NoContent);
        Assert.Empty(await PersistedPermissionsAsync(_factory, member.UserId));
    }

    [Fact]
    public async Task AssignAndRevokeInterleavedByManyCallers_NeverFailWithA5xxAndEndInAConsistentState()
    {
        var (owner, member) = await OwnerWithMemberAsync();
        var manager = await SeedMemberAsync(_factory, _client, owner.TenantId, "manager");
        using (var ownerClient = AuthorizedClient(_factory, owner.OwnerToken))
        {
            await GrantAsync(ownerClient, manager.UserId, Permission.UsersManage);
        }

        for (var round = 0; round < 5; round++)
        {
            // Three different callers (owner, owner, manager) alternate assign/revoke of the same permission.
            var calls = Enumerable.Range(0, 12).Select(i =>
            {
                var token = i % 3 == 2 ? manager.Token : owner.OwnerToken;
                return i % 2 == 0
                    ? Assign(token, member.UserId, Permission.UsersView)
                    : Revoke(token, member.UserId, Permission.UsersView);
            }).ToList();

            var responses = await RunInParallelAsync(calls);

            // Both operations are idempotent, so every interleaving must answer 204 — in particular no 5xx.
            AssertAll(responses, HttpStatusCode.NoContent);

            var persisted = await PersistedPermissionsAsync(_factory, member.UserId);
            Assert.True(persisted.Count <= 1, $"Expected at most one row, found {persisted.Count}.");
            var held = persisted.Contains(Permission.UsersView);

            using var memberClient = AuthorizedClient(_factory, member.Token);
            var profile = await (await memberClient.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<UserProfileResponse>();
            Assert.Equal(held, profile!.Permissions.Contains(Permission.UsersView));
            Assert.Equal(held ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, member.Token));
        }

        // Deterministic end states after the storm: the last sequential writer wins.
        using var finalOwnerClient = AuthorizedClient(_factory, owner.OwnerToken);
        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(finalOwnerClient, member.UserId, Permission.UsersView)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, member.Token));
        await GrantAsync(finalOwnerClient, member.UserId, Permission.UsersView);
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, member.Token));
    }

    [Fact]
    public async Task TargetsRequestsRacingARevoke_OnlyEverSee200Or403_AndAreForbiddenOnceTheRevokeCompleted()
    {
        var (owner, member) = await OwnerWithMemberAsync(Permission.UsersView);
        var calls = Enumerable.Range(0, 30).Select(_ => ProbeUsersView(member.Token)).ToList();
        calls.Insert(10, Revoke(owner.OwnerToken, member.UserId, Permission.UsersView));

        var responses = await RunInParallelAsync(calls);

        Assert.All(responses, r => Assert.NotNull(r));
        Assert.Equal(HttpStatusCode.NoContent, responses[10]);
        var probes = responses.Where((_, i) => i != 10).ToList();
        Assert.All(probes, r => Assert.True(
            r is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"Unexpected status {(int)r!.Value} for a request racing a revoke."));
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, member.Token));
    }

    [Fact]
    public async Task Revoke_ParallelAcrossTwoTenants_OnlyTheCallersOwnTenantIsAffected()
    {
        var (ownerA, memberA) = await OwnerWithMemberAsync(Permission.UsersView);
        var (_, memberB) = await OwnerWithMemberAsync(Permission.UsersView);

        var ownRevokes = Enumerable.Range(0, 5).Select(_ => Revoke(ownerA.OwnerToken, memberA.UserId, Permission.UsersView)).ToList();
        var crossTenantRevokes = Enumerable.Range(0, 5).Select(_ => Revoke(ownerA.OwnerToken, memberB.UserId, Permission.UsersView)).ToList();

        var responses = await RunInParallelAsync(ownRevokes.Concat(crossTenantRevokes));

        Assert.All(responses, r => Assert.NotNull(r));
        Assert.All(responses.Take(5), r => Assert.Equal(HttpStatusCode.NoContent, r));
        Assert.All(responses.Skip(5), r => Assert.Equal(HttpStatusCode.NotFound, r));
        Assert.Empty(await PersistedPermissionsAsync(_factory, memberA.UserId));
        Assert.Equal(new[] { Permission.UsersView }, await PersistedPermissionsAsync(_factory, memberB.UserId));
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeUsersViewAsync(_factory, memberA.Token));
        Assert.Equal(HttpStatusCode.OK, await ProbeUsersViewAsync(_factory, memberB.Token));
    }
}
