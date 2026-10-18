extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>Concurrent sessions of different tenants never see each other's data: no token or profile state is shared.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class MultiUserIsolationTests(TestApiFactory api)
{
    private sealed record Tenant(ApiSeed.Owner Owner, ApiSeed.Member First, ApiSeed.Member Second)
    {
        public IEnumerable<Guid> UserIds => new[] { Owner.UserId, First.UserId, Second.UserId };

        public IEnumerable<string> Emails => new[] { Owner.Email, First.Email, Second.Email };
    }

    [Fact]
    public async Task Ten_interleaved_sessions_each_see_only_their_own_tenant()
    {
        const int sessions = 10;
        const int rounds = 4;
        var tenants = new List<Tenant>();
        for (var i = 0; i < sessions; i++)
        {
            var owner = await ApiSeed.RegisterOwnerAsync(api, $"iso{i}");
            tenants.Add(new Tenant(
                owner,
                await ApiSeed.CreateMemberAsync(api, owner, $"iso{i}-a"),
                await ApiSeed.CreateMemberAsync(api, owner, $"iso{i}-b")));
        }

        using var host = WebTestHost.Create(api);

        await Task.WhenAll(tenants.Select(async tenant =>
        {
            using var session = host.NewSession();
            var login = await WebFlows.LoginAsync(session, tenant.Owner.Email, ApiSeed.StrongPassword);
            login.ShouldRedirectTo("/");

            var foreignEmails = tenants.Where(t => t != tenant).SelectMany(t => t.Emails).ToList();
            var foreignTenantIds = tenants.Where(t => t != tenant).Select(t => t.Owner.TenantId.ToString()).ToList();

            for (var round = 0; round < rounds; round++)
            {
                await Task.Yield();
                var users = await WebFlows.UsersAsync(session);
                var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

                var listed = users.Select(PageContract.UserRow).Select(r => Guid.Parse(r.GetAttribute("data-user-id")!)).Order();
                Assert.Equal(tenant.UserIds.Order(), listed);
                Assert.Equal(tenant.Owner.TenantId.ToString(), home.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
                Assert.Equal("true", home.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value"));
                foreach (var foreign in foreignEmails)
                {
                    Assert.DoesNotContain(foreign, users.Body);
                    Assert.DoesNotContain(foreign, home.Body);
                }

                foreach (var foreignTenant in foreignTenantIds)
                {
                    Assert.DoesNotContain(foreignTenant, users.Body);
                    Assert.DoesNotContain(foreignTenant, home.Body);
                }
            }
        }));

        // Every API read carried the bearer token of exactly the session it served: ten distinct tokens, all used.
        var tokens = host.Api.IssuedTokens;
        Assert.Equal(sessions, tokens.Distinct().Count());
        var bearersUsed = host.Api.Calls.Where(c => c.Method == HttpMethod.Get).Select(c => c.BearerToken).Distinct().ToList();
        Assert.Equal(tokens.Order(), bearersUsed.Order());
    }

    [Fact]
    public async Task Logging_in_concurrently_as_different_users_never_swaps_identities()
    {
        var first = await ApiSeed.RegisterOwnerAsync(api, "swap-a");
        var second = await ApiSeed.RegisterOwnerAsync(api, "swap-b");
        using var host = WebTestHost.Create(api);
        using var firstSession = host.NewSession();
        using var secondSession = host.NewSession();

        await Task.WhenAll(
            WebFlows.LoginAsync(firstSession, first.Email, ApiSeed.StrongPassword),
            WebFlows.LoginAsync(secondSession, second.Email, ApiSeed.StrongPassword));

        var (firstHome, secondHome) = (await firstSession.GetAsync("/"), await secondSession.GetAsync("/"));
        Assert.Equal(first.TenantId.ToString(), firstHome.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
        Assert.Equal(second.TenantId.ToString(), secondHome.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
    }
}
