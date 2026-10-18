extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET /demo: the public template tour, the signed-in "You, right now" panel with its access matrix, and the opt-in demo accounts table.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class DemoTourPageTests(TestApiFactory api)
{
    private static readonly string[] LivePages = DemoFlows.LivePages;

    private static readonly string[] MatrixPermissions = { "UsersView", "UsersManage", "ProductsView", "ProductsManage" };

    // ---- the public tour ----

    [Fact]
    public async Task The_tour_is_public_explains_what_the_template_provides_and_links_to_the_live_pages()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("text/html", page.Header("Content-Type")?.Split(';')[0]);
        DemoFlows.AssertIsTheRealTour(page);
        Assert.Empty(page.Select(PageContract.YouNow));
        Assert.Empty(page.Select(PageContract.AccessCell));
        Assert.DoesNotContain(page.Document.Forms, PageContract.IsLogoutForm);
        Assert.Equal(0, host.Api.CallCount);
        WebFlows.AssertNoStackTrace(page);
    }

    [Fact]
    public async Task The_tour_stays_public_for_forged_credentials()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var forgedCookie = host.NewSession();
        using var bearerOnly = host.NewSession();

        var withCookie = await forgedCookie.GetAsync("/demo", ("Cookie", ".AspNetCore.Cookies=CfDJ8-not-a-real-protected-ticket"));
        var withBearer = await bearerOnly.GetAsync("/demo", ("Authorization", $"Bearer {owner.Token}"));

        withCookie.ShouldBe(HttpStatusCode.OK);
        withBearer.ShouldBe(HttpStatusCode.OK);
        Assert.Empty(withCookie.Select(PageContract.YouNow));
        Assert.Empty(withBearer.Select(PageContract.YouNow));
        Assert.DoesNotContain(owner.TenantId.ToString(), withCookie.Body + withBearer.Body);
        Assert.Equal(0, host.Api.CallCount);
    }

    // ---- "You, right now" ----

    [Fact]
    public async Task A_signed_in_owner_sees_tenant_owner_flag_permissions_and_the_matrix_after_exactly_one_api_call()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var callsBefore = host.Api.CallCount;

        var page = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.NotNull(page.Document.QuerySelector(PageContract.YouNow));
        Assert.Equal(owner.TenantId.ToString(), page.Document.QuerySelector(PageContract.YouTenantId)?.TextContent.Trim());
        Assert.Equal("true", page.Document.QuerySelector(PageContract.YouIsOwner)?.GetAttribute("data-value"));
        Assert.Equal(MatrixPermissions.Order(), page.Select(PageContract.AccessCell).Select(c => c.GetAttribute("data-permission")!).Order());
        Assert.All(page.Select(PageContract.AccessCell), c => Assert.Contains(c.GetAttribute("data-held"), new[] { "true", "false" }));
        var call = Assert.Single(host.Api.Calls.Skip(callsBefore));
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("/api/users/me", call.Uri.AbsolutePath);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", call.Authorization);
        Assert.NotNull(page.FindLogoutForm());
        Assert.True(LivePages.All(DemoFlows.LinkedPaths(page).Contains));
        WebFlows.AssertNotStored(page);
        WebFlows.AssertNoJwtLeaked(session, host);
    }

    [Fact]
    public async Task A_member_sees_exactly_the_permissions_held_in_the_list_and_the_matrix_and_it_follows_grants_and_revokes_live()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "member", Permission.UsersView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var both = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(owner.TenantId.ToString(), both.Document.QuerySelector(PageContract.YouTenantId)?.TextContent.Trim());
        Assert.Equal("false", both.Document.QuerySelector(PageContract.YouIsOwner)?.GetAttribute("data-value"));
        Assert.Equal(new[] { "ProductsManage", "UsersView" }, PermissionNames(both));
        Assert.Equal(new[] { "ProductsManage", "UsersView" }, HeldInMatrix(both));

        await ApiSeed.GrantAsync(api, owner, member.UserId, Permission.ProductsView);
        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.UsersView);
        var changed = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(new[] { "ProductsManage", "ProductsView" }, PermissionNames(changed));
        Assert.Equal(new[] { "ProductsManage", "ProductsView" }, HeldInMatrix(changed));

        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.ProductsManage);
        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.ProductsView);
        var none = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty(PermissionNames(none));
        Assert.Empty(HeldInMatrix(none));
        Assert.Equal(4, none.Select(PageContract.AccessCell).Count());
    }

    [Theory]
    [InlineData("viewer", new[] { "ProductsView" })]
    [InlineData("manager", new[] { "ProductsManage", "ProductsView", "UsersView" })]
    [InlineData("noaccess", new string[0])]
    public async Task The_matrix_of_a_demo_account_matches_what_the_seed_gave_it(string key, string[] held)
    {
        var account = DemoAccounts.Get(key);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, account.Email, DemoAccounts.Password);

        var page = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("false", page.Document.QuerySelector(PageContract.YouIsOwner)?.GetAttribute("data-value"));
        Assert.Equal(held.Order(), PermissionNames(page));
        Assert.Equal(held.Order(), HeldInMatrix(page));
    }

    [Fact]
    public async Task The_tour_keeps_other_users_data_out_for_two_tenants_at_once()
    {
        var first = await ApiSeed.RegisterOwnerAsync(api, "first");
        var second = await ApiSeed.RegisterOwnerAsync(api, "second");
        using var host = WebTestHost.Create(api);
        using var firstSession = await WebFlows.SignedInAsync(host, first.Email);
        using var secondSession = await WebFlows.SignedInAsync(host, second.Email);

        var pages = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? firstSession : secondSession).GetAsync("/demo")));

        for (var i = 0; i < pages.Length; i++)
        {
            var (mine, other) = i % 2 == 0 ? (first, second) : (second, first);
            pages[i].ShouldBe(HttpStatusCode.OK);
            Assert.Equal(mine.TenantId.ToString(), pages[i].Document.QuerySelector(PageContract.YouTenantId)?.TextContent.Trim());
            Assert.DoesNotContain(other.TenantId.ToString(), pages[i].Body);
        }
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task The_tour_degrades_in_a_controlled_way_when_the_profile_cannot_be_loaded(string failure)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync("/demo");

        Assert.Contains(page.Status, new[] { 200, 503 });
        Assert.Empty(page.Select(PageContract.YouNow));
        Assert.DoesNotContain(owner.TenantId.ToString(), page.Body);
        WebFlows.AssertNoStackTrace(page);
        Assert.False(PageResponse.ContainsJwt(page.Body));
        Assert.Contains(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/users/me");
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
        host.Api.Intercept = null;
        Assert.NotNull((await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK).Document.QuerySelector(PageContract.YouNow));
    }

    public static TheoryData<string> BackendFailures => FakeApi.RepresentativeBackendFailures;

    [Fact]
    public async Task An_expired_token_never_shows_the_profile_panel_and_ends_in_a_controlled_response()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/users/me")
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var page = await session.GetAsync("/demo");

        Assert.Empty(page.Select(PageContract.YouNow));
        Assert.DoesNotContain(owner.TenantId.ToString(), page.Body);
        WebFlows.AssertNoStackTrace(page);
        if (page.IsRedirect)
        {
            page.ShouldRedirectToLogin();
            Assert.True(WebFlows.ClearsAuthCookie(page));
        }
        else
        {
            Assert.Equal(200, page.Status);
        }
    }

    // ---- the demo accounts table (Demo:ShowAccounts) ----

    [Fact]
    public async Task With_show_accounts_on_the_tour_lists_the_five_accounts_with_the_shared_password_and_scenarios_to_anyone()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var anonymous = host.NewSession();

        var page = (await anonymous.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(DemoAccounts.AllEmails.Order(), DemoFlows.TableEmails(page).Order());
        Assert.Contains(SpanishText.DemoAccountsHeading, page.Text, StringComparison.OrdinalIgnoreCase);
        foreach (var row in page.Select(PageContract.DemoAccountRow))
        {
            var email = row.GetAttribute("data-email")!;
            Assert.Contains(email, row.TextContent);
            Assert.False(string.IsNullOrWhiteSpace(row.TextContent.Replace(email, string.Empty)), $"The row of {email} describes neither a role nor permissions.");
        }

        var passwords = DemoFlows.DisplayedPasswords(page);
        Assert.NotEmpty(passwords);
        Assert.All(passwords, p => Assert.Equal(DemoAccounts.Password, p));
        Assert.NotEmpty(page.Select(PageContract.DemoScenario));
        Assert.All(page.Select(PageContract.DemoScenario), s => Assert.False(string.IsNullOrWhiteSpace(s.TextContent)));
        Assert.Equal(0, host.Api.CallCount);
        Assert.Empty(page.Select(PageContract.YouNow));
    }

    [Fact]
    public async Task With_show_accounts_on_a_signed_in_visitor_gets_both_the_table_and_their_own_panel_in_one_api_call()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.CreateDemo(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var callsBefore = host.Api.CallCount;

        var page = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(DemoAccounts.AllEmails.Order(), DemoFlows.TableEmails(page).Order());
        Assert.Equal(owner.TenantId.ToString(), page.Document.QuerySelector(PageContract.YouTenantId)?.TextContent.Trim());
        Assert.Equal("/api/users/me", Assert.Single(host.Api.Calls.Skip(callsBefore)).Uri.AbsolutePath);
        Assert.NotNull(page.FindLogoutForm());
    }

    [Fact]
    public async Task A_custom_demo_password_is_what_the_tour_shows_and_the_default_is_not()
    {
        const string custom = "Custom-Pass-2027-Zq";
        using var host = WebTestHost.CreateDemo(api, custom);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.NotEmpty(DemoFlows.DisplayedPasswords(page));
        Assert.All(DemoFlows.DisplayedPasswords(page), p => Assert.Equal(custom, p));
        Assert.DoesNotContain(DemoAccounts.Password, page.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    public async Task With_show_accounts_off_no_page_shows_an_account_email_or_the_password(string? setting)
    {
        const string customPassword = "Custom-Pass-2027-Zq";
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var product = await ApiProducts.CreateAsync(api, owner.Token);
        using var host = WebTestHost.Create(api, settings: new WebSettings("Production", setting, customPassword));
        using var anonymous = host.NewSession();
        using var signedIn = await WebFlows.SignedInAsync(host, owner.Email);

        var tour = (await anonymous.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);
        DemoFlows.AssertIsTheRealTour(tour); // precondition: it is the real tour, not an empty page
        var pages = new List<PageResponse>
        {
            tour,
            (await anonymous.GetAsync("/")).ShouldBe(HttpStatusCode.OK),
            (await anonymous.GetAsync("/login")).ShouldBe(HttpStatusCode.OK),
            (await anonymous.GetAsync("/register")).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync("/")).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync("/users")).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync("/products")).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync(ProductFlows.EditUrl(product.Id))).ShouldBe(HttpStatusCode.OK),
            (await signedIn.GetAsync(ProductFlows.DeleteUrl(product.Id))).ShouldBe(HttpStatusCode.OK),
            await anonymous.GetAsync("/no-such-page"),
        };

        Assert.NotNull(pages[4].Document.QuerySelector(PageContract.YouNow));
        Assert.NotNull(pages[2].FindPasswordForm());
        foreach (var page in pages)
        {
            DemoFlows.AssertNoDemoAccountMaterial(page, customPassword);
        }
    }

    // ---- helpers ----

    private static string[] PermissionNames(PageResponse page)
        => page.Select(PageContract.YouPermission).Select(e => e.GetAttribute("data-permission") ?? string.Empty).Order().ToArray();

    private static string[] HeldInMatrix(PageResponse page)
        => page.Select(PageContract.AccessCell).Where(c => c.GetAttribute("data-held") == "true").Select(c => c.GetAttribute("data-permission")!).Order().ToArray();
}
