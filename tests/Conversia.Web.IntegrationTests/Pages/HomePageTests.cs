extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET /: anonymous landing page, or the caller's profile from GET /api/users/me.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class HomePageTests(TestApiFactory api)
{
    [Fact]
    public async Task Anonymous_home_links_to_login_and_register_and_shows_no_profile()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var targets = page.Select("a[href]")
            .Select(a => new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath)
            .ToList();
        Assert.Contains("/login", targets);
        Assert.Contains("/register", targets);
        Assert.Empty(page.Select(PageContract.ProfileTenantId));
        Assert.DoesNotContain(page.Document.Forms, f => (f.GetAttribute("action") ?? "").EndsWith("/logout"));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task Owner_home_shows_tenant_owner_flag_and_the_permissions_the_api_reports()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(owner.TenantId.ToString(), page.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
        Assert.Equal("true", page.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value"));
        Assert.Empty(page.Select(PageContract.ProfilePermission));
        // Filtered, not Single: a profile fetch right after sign-in is allowed.
        var meCall = host.Api.Calls.Last(c => c.PathAndQuery == "/api/users/me");
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", meCall.Authorization);
        Assert.NotNull(page.FindLogoutForm());
    }

    [Fact]
    public async Task Member_home_lists_exactly_the_permissions_held_and_follows_grants_and_revokes_live()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "member", Permission.UsersView, Permission.UsersManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var both = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("false", both.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value"));
        Assert.Equal(owner.TenantId.ToString(), both.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
        Assert.Equal(new[] { "UsersManage", "UsersView" }, PermissionNames(both));

        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.UsersManage);
        var one = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(new[] { "UsersView" }, PermissionNames(one));

        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.UsersView);
        var none = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty(PermissionNames(none));
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Home_degrades_to_an_unavailable_message_when_the_profile_cannot_be_loaded(string failure)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync("/");

        Assert.Contains((int)page.StatusCode, new[] { 200, 503 });
        Assert.Contains(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Select(PageContract.ProfileTenantId));
        WebFlows.AssertNoStackTrace(page);
        Assert.Contains(host.Api.Calls, c => c.PathAndQuery == "/api/users/me");
    }

    public static TheoryData<string> BackendFailures => FakeApi.BackendFailures;

    private static string[] PermissionNames(PageResponse page)
        => page.Select(PageContract.ProfilePermission).Select(e => e.GetAttribute("data-permission") ?? string.Empty).Order().ToArray();
}
