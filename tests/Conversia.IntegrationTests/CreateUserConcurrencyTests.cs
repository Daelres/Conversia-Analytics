using System.Net;
using System.Net.Http.Json;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Concurrency/multi-user behaviour of <c>POST /api/users</c> against the
/// real PostgreSQL: the global unique index on <c>Users.Email</c> is the
/// source of truth, so racing creations must yield exactly one winner and
/// controlled 409s — never a 5xx and never a duplicate row.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class CreateUserConcurrencyTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CreateUserConcurrencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private enum Origin { RegisterTenant, CreateUser }

    private Func<Task<HttpResponseMessage>> Create(string token, string email, string password = StrongPassword) => async () =>
    {
        using var client = AuthorizedClient(_factory, token);
        return await CreateUserAsync(client, email, password);
    };

    private static void AssertExactlyOneCreatedRestConflict(HttpStatusCode?[] responses)
    {
        Assert.All(responses, r => Assert.NotNull(r));
        Assert.Equal(1, responses.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(responses.Length - 1, responses.Count(r => r == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task CreateUser_20ConcurrentRequestsWithSameEmail_ExactlyOneSucceedsAndRestConflict()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var email = UniqueEmail("race");

        var responses = await RunInParallelAsync(Enumerable.Range(0, 20).Select(_ => Create(owner.OwnerToken, email)));

        AssertExactlyOneCreatedRestConflict(responses);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(owner.TenantId, persisted.TenantId);
    }

    [Fact]
    public async Task CreateUser_ConcurrentRequestsWithSameEmailFromTwoTenants_ExactlyOneTenantWins()
    {
        var tenantA = await RegisterTenantSessionAsync(_client, "tenantA");
        var tenantB = await RegisterTenantSessionAsync(_client, "tenantB");
        var email = UniqueEmail("cross-race");

        var responses = await RunInParallelAsync(Enumerable.Range(0, 20)
            .Select(i => Create(i % 2 == 0 ? tenantA.OwnerToken : tenantB.OwnerToken, email)));

        AssertExactlyOneCreatedRestConflict(responses);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Contains(persisted.TenantId, new[] { tenantA.TenantId, tenantB.TenantId });
        Assert.False(persisted.IsOwner);
    }

    [Fact]
    public async Task CreateUser_30ConcurrentRequestsWithDistinctEmails_AllSucceedAndAllLandInTheCallersTenant()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var emails = Enumerable.Range(0, 30).Select(_ => UniqueEmail("load")).ToList();

        var responses = await RunInParallelAsync(emails.Select(e => Create(owner.OwnerToken, e)));

        Assert.All(responses, r => Assert.NotNull(r));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r));

        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var listed = await ListUsersAsync(ownerClient);
        Assert.Equal(emails.Count + 1, listed.Count); // + the owner
        Assert.Equal(listed.Count, listed.Select(u => u.UserId).Distinct().Count());
        foreach (var email in emails)
        {
            var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
            Assert.Equal(owner.TenantId, persisted.TenantId);
            Assert.False(persisted.IsOwner);
        }
    }

    [Fact]
    public async Task CreateUser_TwoCallersInParallel_EachCreatedUserRecordsItsOwnCreator()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var manager = await SeedMemberAsync(_factory, _client, owner.TenantId, "manager");
        await GrantAsync(ownerClient, manager.UserId, Permission.UsersManage);

        var byOwner = Enumerable.Range(0, 10).Select(_ => UniqueEmail("by-owner")).ToList();
        var byManager = Enumerable.Range(0, 10).Select(_ => UniqueEmail("by-manager")).ToList();

        var responses = await RunInParallelAsync(
            byOwner.Select(e => Create(owner.OwnerToken, e)).Concat(byManager.Select(e => Create(manager.Token, e))));

        Assert.All(responses, r => Assert.NotNull(r));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r));
        foreach (var email in byOwner)
        {
            Assert.Equal(owner.OwnerId, Assert.Single(await FindUsersByEmailAsync(_factory, email)).CreatedBy);
        }

        foreach (var email in byManager)
        {
            Assert.Equal(manager.UserId, Assert.Single(await FindUsersByEmailAsync(_factory, email)).CreatedBy);
        }
    }

    [Fact]
    public async Task EmailRacedBetweenRegisterTenantAndCreateUser_ExactlyOneWinsEveryRound()
    {
        var owner = await RegisterTenantSessionAsync(_client);

        for (var round = 0; round < 8; round++)
        {
            var email = UniqueEmail($"mixed-race-{round}");
            var calls = new List<(Origin Origin, Func<Task<HttpResponseMessage>> Call)>();
            for (var i = 0; i < 3; i++)
            {
                calls.Add((Origin.RegisterTenant, async () =>
                {
                    using var client = _factory.CreateClient();
                    return await client.PostAsJsonAsync("/api/auth/register-tenant", new
                    {
                        tenantName = UniqueTenantName("Race Co"),
                        email,
                        password = StrongPassword
                    });
                }));
                calls.Add((Origin.CreateUser, Create(owner.OwnerToken, email)));
            }

            // RunInParallelAsync preserves call order, so calls[i] produced statuses[i].
            var statuses = await RunInParallelAsync(calls.Select(c => c.Call));

            AssertExactlyOneCreatedRestConflict(statuses);
            var winner = calls[Array.IndexOf(statuses, HttpStatusCode.Created)].Origin;
            var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
            if (winner == Origin.RegisterTenant)
            {
                Assert.True(persisted.IsOwner);
                Assert.NotEqual(owner.TenantId, persisted.TenantId);
            }
            else
            {
                Assert.False(persisted.IsOwner);
                Assert.Equal(owner.TenantId, persisted.TenantId);
            }
        }
    }

    [Fact]
    public async Task CreateUser_ConcurrentMixOfValidAndInvalidRequests_EachGetsItsOwnOutcome()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var validEmails = Enumerable.Range(0, 10).Select(_ => UniqueEmail("mix-ok")).ToList();
        var invalidEmails = Enumerable.Range(0, 10).Select(_ => UniqueEmail("mix-weak")).ToList();

        var responses = await RunInParallelAsync(
            validEmails.Select(e => Create(owner.OwnerToken, e))
                .Concat(invalidEmails.Select(e => Create(owner.OwnerToken, e, "weakpassword"))));

        Assert.All(responses, r => Assert.NotNull(r));
        Assert.All(responses.Take(10), r => Assert.Equal(HttpStatusCode.Created, r));
        Assert.All(responses.Skip(10), r => Assert.Equal(HttpStatusCode.BadRequest, r));
        foreach (var email in validEmails)
        {
            Assert.Single(await FindUsersByEmailAsync(_factory, email));
        }

        foreach (var email in invalidEmails)
        {
            Assert.Empty(await FindUsersByEmailAsync(_factory, email));
        }
    }
}
