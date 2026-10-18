extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET/POST /users: tenant user list, user creation, permission grant/revoke — all authorised by the API.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class UsersPageTests(TestApiFactory api)
{
    private const string NewUserPassword = "N3w-User-Passw0rd!";

    // ---- authentication gate ----

    [Fact]
    public async Task Anonymous_users_page_redirects_to_login_with_the_return_url_and_leaks_nothing()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync("/users");

        Assert.Equal("/users", response.ShouldRedirectToLogin());
        Assert.DoesNotContain(owner.Email, response.Body);
        Assert.Empty(response.Select(PageContract.UserRow));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task Forged_cookies_and_a_bearer_header_do_not_authenticate_against_the_web()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var forgedCookie = host.NewSession();
        using var bearerOnly = host.NewSession();

        var withCookie = await forgedCookie.GetAsync("/users", ("Cookie", ".AspNetCore.Cookies=CfDJ8-not-a-real-protected-ticket"));
        var withBearer = await bearerOnly.GetAsync("/users", ("Authorization", $"Bearer {owner.Token}"));

        Assert.Equal("/users", withCookie.ShouldRedirectToLogin());
        Assert.Equal("/users", withBearer.ShouldRedirectToLogin());
        Assert.DoesNotContain(owner.Email, withCookie.Body + withBearer.Body);
        Assert.Equal(0, host.Api.CallCount);
    }

    // ---- listing ----

    [Fact]
    public async Task Owner_sees_exactly_the_tenants_users_with_the_owner_flag()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var first = await ApiSeed.CreateMemberAsync(api, owner, "first");
        var second = await ApiSeed.CreateMemberAsync(api, owner, "second");
        var stranger = await ApiSeed.RegisterOwnerAsync(api, "stranger");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await WebFlows.UsersAsync(session);

        var rows = page.Select(PageContract.UserRow).ToDictionary(r => Guid.Parse(r.GetAttribute("data-user-id")!));
        Assert.Equal(new[] { owner.UserId, first.UserId, second.UserId }.Order(), rows.Keys.Order());
        Assert.Contains(owner.Email, rows[owner.UserId].TextContent);
        Assert.Contains(first.Email, rows[first.UserId].TextContent);
        Assert.Contains(second.Email, rows[second.UserId].TextContent);
        Assert.NotNull(rows[owner.UserId].QuerySelector(PageContract.OwnerBadge));
        Assert.Null(rows[first.UserId].QuerySelector(PageContract.OwnerBadge));
        Assert.Null(rows[second.UserId].QuerySelector(PageContract.OwnerBadge));
        Assert.DoesNotContain(stranger.Email, page.Body);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", host.Api.Calls.Last(c => c.PathAndQuery == "/api/users").Authorization);
    }

    [Fact]
    public async Task User_emails_are_html_encoded()
    {
        const string hostile = "<img/src=x/onerror=alert(1)>&amp;\"'@example.test";
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var hostileId = await ApiSeed.SeedUserWithRawEmailAsync(api, owner.TenantId, hostile);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await WebFlows.UsersAsync(session);

        Assert.DoesNotContain("<img/src=x", page.Body);
        Assert.Empty(page.Select("img"));
        var row = Assert.Single(page.Select(PageContract.UserRow), r => r.GetAttribute("data-user-id") == hostileId.ToString());
        Assert.Contains(hostile, row.TextContent);
    }

    [Fact]
    public async Task Caller_without_view_permission_gets_403_not_authorized_and_no_data()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "noperm");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var page = await session.GetAsync("/users");

        page.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.Email, page.Body); // the member's own email may appear (a layout may show who is signed in)
        Assert.Empty(page.Select(PageContract.UserRow));
        Assert.Equal(HttpStatusCode.Forbidden, host.Api.Calls.Last(c => c.PathAndQuery == "/api/users").Status);
        WebFlows.AssertNoStackTrace(page);
    }

    [Fact]
    public async Task Api_401_for_an_expired_token_signs_the_user_out_and_sends_them_to_login()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/users")
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var expired = await session.GetAsync("/users");

        expired.ShouldRedirectToLogin();
        Assert.True(WebFlows.ClearsAuthCookie(expired), "The response must clear the authentication cookie.");
        Assert.False(session.HasAuthCookie);
        Assert.DoesNotContain(owner.Email, expired.Body);

        var callsBefore = host.Api.CallCount;
        (await session.GetAsync("/users")).ShouldRedirectToLogin();
        Assert.Equal(callsBefore, host.Api.CallCount);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Users_page_degrades_to_an_unavailable_message_when_the_api_misbehaves(string failure)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync("/users");

        Assert.Contains((int)page.StatusCode, new[] { 200, 503 });
        Assert.Contains(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Select(PageContract.UserRow));
        WebFlows.AssertNoStackTrace(page);
        Assert.Contains(host.Api.Calls, c => c.PathAndQuery == "/api/users");
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
    }

    public static TheoryData<string> BackendFailures => FakeApi.BackendFailures;

    // ---- create user ----

    [Fact]
    public async Task Owner_creates_a_user_and_the_list_shows_them()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var email = ApiSeed.UniqueEmail("created");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var result = await SubmitCreateUserAsync(session, email, NewUserPassword);

        result.ShouldBe(HttpStatusCode.OK);
        Assert.DoesNotContain(NewUserPassword, result.Body);
        Assert.Equal(1, await ApiSeed.CountUsersWithEmailAsync(api, email));
        using var apiClient = ApiSeed.AuthorizedClient(api, owner.Token);
        var created = Assert.Single(await ListApiUsersAsync(apiClient), u => u.Email == email);
        Assert.False(created.IsOwner);
        var row = Assert.Single(result.Select(PageContract.UserRow), r => r.GetAttribute("data-user-id") == created.UserId.ToString());
        Assert.Contains(email, row.TextContent);
        Assert.Null(row.QuerySelector(PageContract.OwnerBadge));
        var post = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/users");
        Assert.Equal(HttpStatusCode.Created, post.Status);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", post.Authorization);
        WebFlows.AssertNoSecretLogged(host, NewUserPassword);
    }

    [Fact]
    public async Task Creating_a_user_with_a_taken_email_shows_the_conflict_inline()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var existing = await ApiSeed.CreateMemberAsync(api, owner, "taken");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var result = await SubmitCreateUserAsync(session, existing.Email, NewUserPassword);

        result.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.EmailTaken, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NewUserPassword, result.Body);
        Assert.Equal(1, await ApiSeed.CountUsersWithEmailAsync(api, existing.Email));
        Assert.Contains(existing.Email, result.Text);
    }

    [Fact]
    public async Task Creating_a_user_with_a_weak_password_shows_the_apis_policy_message()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var email = ApiSeed.UniqueEmail("weak");
        string policyMessage;
        using (var apiClient = ApiSeed.AuthorizedClient(api, owner.Token))
        {
            using var reference = await apiClient.PostAsJsonAsync("/api/users", new { email, password = "Zq9xkWeak" });
            Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
            var problem = await reference.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
            policyMessage = Assert.Single(problem!.Errors["Password"]);
        }

        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var result = await SubmitCreateUserAsync(session, email, "Zq9xkWeak");

        result.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(policyMessage, result.Text);
        Assert.DoesNotContain("Zq9xkWeak", result.Body);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
    }

    [Theory]
    [InlineData("owner", true, false)]
    [InlineData("owner", false, true)]
    [InlineData("owner", true, true)]
    [InlineData("manager", true, false)]
    [InlineData("manager", false, true)]
    [InlineData("manager", true, true)]
    public async Task A_manager_submitting_input_the_web_rejects_locally_still_sees_the_list_the_visible_form_and_a_visible_message(
        string caller, bool malformedEmail, bool emptyPassword)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var existing = await ApiSeed.CreateMemberAsync(api, owner, "existing", Permission.UsersView);
        var manager = await ApiSeed.CreateMemberAsync(api, owner, "manager", Permission.UsersView, Permission.UsersManage);
        var email = malformedEmail ? "not-an-email" : ApiSeed.UniqueEmail("rejected");
        var password = emptyPassword ? string.Empty : NewUserPassword;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, caller == "owner" ? owner.Email : manager.Email);
        var pristine = await WebFlows.UsersAsync(session);
        var form = FormModel.From(pristine, pristine.FindPasswordForm()).Set("email", email).Set("password", password);
        var callsBefore = host.Api.CallCount;

        var response = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        Assert.DoesNotContain(NewUserPassword, response.Body);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/users");
        Assert.True(session.HasAuthCookie, "A rejected form is not a reason to sign the user out.");
        Assert.Empty(WebFlows.IssuedCookies(response));
        Assert.False(WebFlows.ClearsAuthCookie(response), "A rejected form must not clear the authentication cookie.");
        if (!malformedEmail)
        {
            Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        }

        using var apiClient = ApiSeed.AuthorizedClient(api, owner.Token);
        Assert.Equal(3, (await ListApiUsersAsync(apiClient)).Count);

        // The list is shown: every tenant user, in rows a person can see.
        var visibleRows = response.Select(PageContract.UserRow).Where(r => !IsHidden(r)).ToList();
        Assert.Equal(new[] { owner.UserId, existing.UserId, manager.UserId }.Order(), visibleRows.Select(r => Guid.Parse(r.GetAttribute("data-user-id")!)).Order());

        // The form is shown and still usable: not hidden, and it can be submitted again.
        var shownForm = response.FindPasswordForm();
        Assert.False(IsHidden(shownForm), "The create-user form must not be hidden after a rejected submit.");
        Assert.True(PageContract.IsUsable(shownForm), "The create-user form must still offer a usable submit control.");
        Assert.True(string.IsNullOrEmpty(shownForm.QuerySelector("input[type=password]")!.GetAttribute("value")), "The password input must be re-rendered empty.");

        // A message is shown: the form's visible text differs from the pristine form's (the echoed values are attributes, not text).
        Assert.NotEqual(VisibleText(pristine.FindPasswordForm()), VisibleText(shownForm));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_view_only_caller_forging_a_create_post_the_web_rejects_locally_gets_the_list_and_no_management_controls(
        bool malformedEmail, bool emptyPassword)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "viewer", Permission.UsersView);
        var email = malformedEmail ? "not-an-email" : ApiSeed.UniqueEmail("rejected");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        var ownerPage = await WebFlows.UsersAsync(ownerSession);
        var forged = FormModel.From(ownerPage, ownerPage.FindPasswordForm())
            .WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession))
            .Set("email", email).Set("password", emptyPassword ? string.Empty : NewUserPassword);
        var callsBefore = host.Api.CallCount;

        var response = await WebFlows.FollowRedirectsAsync(viewerSession, await viewerSession.SubmitAsync(forged));

        Assert.Contains(response.Status, new[] { 200, 403 });
        WebFlows.AssertNoStackTrace(response);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/users");
        Assert.True(viewerSession.HasAuthCookie, "A rejected form is not a reason to sign the user out.");
        Assert.Empty(WebFlows.IssuedCookies(response));
        Assert.False(WebFlows.ClearsAuthCookie(response), "A rejected form must not clear the authentication cookie.");
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        var visibleRows = response.Select(PageContract.UserRow).Where(r => !IsHidden(r)).ToList();
        Assert.Equal(new[] { owner.UserId, viewer.UserId }.Order(), visibleRows.Select(r => Guid.Parse(r.GetAttribute("data-user-id")!)).Order());
        Assert.Empty(ActionForms(response));
        Assert.DoesNotContain(response.Select("input[type=password]"), input => !IsHidden(input));
    }

    // ---- who may act ----

    [Fact]
    public async Task Owner_and_manage_holder_get_usable_forms_while_view_only_callers_get_none()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "viewer", Permission.UsersView);
        var manager = await ApiSeed.CreateMemberAsync(api, owner, "manager", Permission.UsersView, Permission.UsersManage);
        using var host = WebTestHost.Create(api);

        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        var ownerPage = await WebFlows.UsersAsync(ownerSession);
        Assert.Contains(ownerPage.UsableForms(), f => f.QuerySelector("input[type=password]") is not null);
        Assert.NotNull(ownerPage.FindPermissionForm("grant", viewer.UserId, "UsersManage"));
        Assert.NotNull(ownerPage.FindPermissionForm("revoke", viewer.UserId, "UsersView"));

        using var managerSession = await WebFlows.SignedInAsync(host, manager.Email);
        var managerPage = await WebFlows.UsersAsync(managerSession);
        Assert.Contains(managerPage.UsableForms(), f => f.QuerySelector("input[type=password]") is not null);
        Assert.NotNull(managerPage.FindPermissionForm("grant", viewer.UserId, "UsersManage"));

        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        var viewerPage = await WebFlows.UsersAsync(viewerSession);
        Assert.Equal(3, viewerPage.Select(PageContract.UserRow).Count());
        Assert.Empty(ActionForms(viewerPage));
    }

    [Fact]
    public async Task A_forged_create_post_from_a_view_only_caller_is_refused_by_the_api_with_an_inline_message()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "viewer", Permission.UsersView);
        var email = ApiSeed.UniqueEmail("forged");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);

        var ownerPage = await WebFlows.UsersAsync(ownerSession);
        var legitimateForm = FormModel.From(ownerPage, ownerPage.FindPasswordForm());
        var viewerToken = await WebFlows.AntiforgeryTokenAsync(viewerSession);
        var forged = legitimateForm.WithAntiforgeryToken(viewerToken).Set("email", email).Set("password", NewUserPassword);
        var callsBefore = host.Api.CallCount;

        var response = await WebFlows.FollowRedirectsAsync(viewerSession, await viewerSession.SubmitAsync(forged));

        Assert.Contains((int)response.StatusCode, new[] { 200, 403 });
        Assert.Contains(SpanishText.NotAuthorized, response.Text, StringComparison.OrdinalIgnoreCase);
        WebFlows.AssertNoStackTrace(response);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        var attempt = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
    }

    // ---- grant / revoke ----

    [Fact]
    public async Task Granting_through_the_ui_persists_the_permission_and_the_member_can_then_view_users()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "grantee");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var memberSession = await WebFlows.SignedInAsync(host, member.Email);
        (await memberSession.GetAsync("/users")).ShouldBe(HttpStatusCode.Forbidden);

        var result = await SubmitPermissionFormAsync(ownerSession, "grant", member.UserId, "UsersView");

        result.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, member.UserId));
        var call = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == $"/api/users/{member.UserId}/permissions");
        Assert.Equal(HttpStatusCode.NoContent, call.Status);
        (await memberSession.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_through_the_ui_removes_the_permission_and_the_member_loses_access()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "revokee", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var memberSession = await WebFlows.SignedInAsync(host, member.Email);
        (await memberSession.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);

        var result = await SubmitPermissionFormAsync(ownerSession, "revoke", member.UserId, "UsersView");

        result.ShouldBe(HttpStatusCode.OK);
        Assert.Empty(await ApiSeed.PersistedPermissionsAsync(api, member.UserId));
        var call = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, call.Status);
        Assert.StartsWith($"/api/users/{member.UserId}/permissions/", call.PathAndQuery);
        (await memberSession.GetAsync("/users")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Submitting_a_stale_grant_or_revoke_form_is_idempotent_and_not_an_error()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "idem");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        // The member holds nothing, so the page naturally offers to grant UsersManage. Someone else grants it first.
        var beforeGrant = await WebFlows.UsersAsync(session);
        var staleGrant = FormModel.From(beforeGrant, beforeGrant.FindPermissionForm("grant", member.UserId, "UsersManage"));
        await ApiSeed.GrantAsync(api, owner, member.UserId, Permission.UsersManage);

        var grantAgain = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(staleGrant));

        AssertNoErrorShown(grantAgain);
        Assert.Equal(new[] { Permission.UsersManage }, await ApiSeed.PersistedPermissionsAsync(api, member.UserId));

        // Now the page naturally offers to revoke it. Someone else revokes it first.
        var beforeRevoke = await WebFlows.UsersAsync(session);
        var staleRevoke = FormModel.From(beforeRevoke, beforeRevoke.FindPermissionForm("revoke", member.UserId, "UsersManage"));
        await ApiSeed.RevokeAsync(api, owner, member.UserId, Permission.UsersManage);

        var revokeAgain = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(staleRevoke));

        AssertNoErrorShown(revokeAgain);
        Assert.Empty(await ApiSeed.PersistedPermissionsAsync(api, member.UserId));
        var staleCalls = host.Api.Calls.Where(c => c.Method != HttpMethod.Get && c.PathAndQuery.Contains("/permissions")).ToList();
        Assert.Equal(2, staleCalls.Count);
        Assert.All(staleCalls, c => Assert.Equal(HttpStatusCode.NoContent, c.Status));
        (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Permission", "999")]
    [InlineData("Permission", "0")]
    [InlineData("Permission", "NotAPermission")]
    [InlineData("Permission", "")]
    [InlineData("UserId", "not-a-guid")]
    [InlineData("UserId", "")]
    [InlineData("UserId", "00000000-0000-0000-0000-000000000000")]
    public async Task Forged_grant_and_revoke_values_end_in_a_controlled_response_and_change_nothing(string field, string forgedValue)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "forged", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        // The forms that are naturally on the page for this state: UsersView is held (so it can be revoked)
        // and UsersManage is not (so it can be granted). The tests forge one field of each.
        foreach (var (action, permission) in new[] { ("revoke", "UsersView"), ("grant", "UsersManage") })
        {
            var page = await WebFlows.UsersAsync(session);
            var form = FormModel.From(page, page.FindPermissionForm(action, member.UserId, permission)).Set(field, forgedValue);
            var callsBefore = host.Api.CallCount;

            var response = await session.SubmitAsync(form);

            await AssertControlledAsync(host, session, response, callsBefore, owner.UserId, member.UserId);
        }

        // Nothing moved: the seeded state is exactly what is persisted.
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, member.UserId));
        Assert.Empty(await ApiSeed.PersistedPermissionsAsync(api, owner.UserId));
    }

    [Fact]
    public async Task A_forged_cross_tenant_grant_cannot_reach_another_tenants_user()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "mine");
        var otherOwner = await ApiSeed.RegisterOwnerAsync(api, "other");
        var victim = await ApiSeed.CreateMemberAsync(api, otherOwner, "victim");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = await WebFlows.UsersAsync(session);
        var form = FormModel.From(page, page.FindPermissionForm("grant", member.UserId, "UsersManage"))
            .Set("UserId", victim.UserId.ToString());

        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        await AssertControlledAsync(host, session, response, callsBefore, owner.UserId, member.UserId);
        Assert.DoesNotContain(victim.Email, response.Body);
        Assert.Empty(await ApiSeed.PersistedPermissionsAsync(api, victim.UserId));
        var attempt = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == $"/api/users/{victim.UserId}/permissions");
        Assert.Equal(HttpStatusCode.NotFound, attempt.Status);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task A_forged_permission_post_from_a_view_only_caller_is_refused_by_the_api_with_an_inline_message(string action)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "viewer", Permission.UsersView);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "target", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);

        // The owner's page has both forms for the target (UsersView is held, UsersManage is not); the viewer's page has neither.
        var ownerPage = await WebFlows.UsersAsync(ownerSession);
        var legitimateForm = FormModel.From(ownerPage, ownerPage.FindPermissionForm(action, target.UserId, action == "grant" ? "UsersManage" : "UsersView"));
        var forged = legitimateForm.WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession));
        var callsBefore = host.Api.CallCount;

        var response = await WebFlows.FollowRedirectsAsync(viewerSession, await viewerSession.SubmitAsync(forged));

        Assert.Contains((int)response.StatusCode, new[] { 200, 403 });
        Assert.Contains(SpanishText.NotAuthorized, response.Text, StringComparison.OrdinalIgnoreCase);
        WebFlows.AssertNoStackTrace(response);
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, target.UserId));
        var attempt = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method != HttpMethod.Get && c.PathAndQuery.StartsWith($"/api/users/{target.UserId}/permissions"));
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
        Assert.True(viewerSession.HasAuthCookie, "A refused action is not a reason to sign the user out.");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task Api_401_on_a_post_action_signs_the_user_out_and_sends_them_to_login(string action)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "target", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = await WebFlows.UsersAsync(session);
        var form = ActionForm(page, action, target.UserId, ApiSeed.UniqueEmail("expired"));
        host.Api.Intercept = request => request.Method == HttpMethod.Get
            ? Task.FromResult<HttpResponseMessage?>(null)
            : FakeApi.Respond(HttpStatusCode.Unauthorized);

        var response = await session.SubmitAsync(form);
        var landing = await WebFlows.FollowRedirectsAsync(session, response);

        Assert.False(session.HasAuthCookie, "The expired session's authentication cookie must be cleared.");
        Assert.Equal("/login", landing.RequestUri.AbsolutePath);
        landing.ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(landing.FindPasswordForm());
        Assert.DoesNotContain(owner.Email, landing.Body);
        (await session.GetAsync("/users")).ShouldRedirectToLogin();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task Anonymous_post_to_an_action_with_a_valid_antiforgery_token_reaches_no_api_and_changes_nothing(string action)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "target", Permission.UsersView);
        var email = ApiSeed.UniqueEmail("anonymous");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, owner.Email);
        var form = ActionForm(await WebFlows.UsersAsync(ownerSession), action, target.UserId, email);
        using var anonymous = host.NewSession();
        var anonymousToken = (await anonymous.GetAsync("/login")).ShouldBe(HttpStatusCode.OK).AntiforgeryToken();
        form.WithAntiforgeryToken(anonymousToken!);
        var callsBefore = host.Api.CallCount;

        var response = await anonymous.SubmitAsync(form);

        if (response.IsRedirect)
        {
            response.ShouldRedirectToLogin();
        }
        else
        {
            Assert.InRange(response.Status, 400, 499);
        }

        WebFlows.AssertNoStackTrace(response);
        WebFlows.AssertNoCookieIssued(anonymous, response);
        Assert.DoesNotContain(owner.Email, response.Body);
        Assert.Empty(response.Select(PageContract.UserRow));
        Assert.Equal(callsBefore, host.Api.CallCount);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, target.UserId));
    }

    public static TheoryData<string, string> ActionFailures
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var failure in FakeApi.BackendFailureNames)
            {
                data.Add("create", failure);
            }

            // A 200 (or a 400 page) in answer to a grant/revoke is ambiguous for a call whose success is a bodyless 204: only the unambiguous failures here.
            foreach (var failure in new[] { "connection refused", "timeout", "500 problem", "503 empty", "502 html" })
            {
                data.Add("grant", failure);
                data.Add("revoke", failure);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ActionFailures))]
    public async Task Backend_failures_on_an_action_show_unavailable_without_crashing_or_signing_out(string action, string failure)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "target", Permission.UsersView);
        var email = ApiSeed.UniqueEmail("unavailable");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var form = ActionForm(await WebFlows.UsersAsync(session), action, target.UserId, email);
        host.Api.Intercept = FakeApi.Failure(failure);
        var callsBefore = host.Api.CallCount;

        var response = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));

        Assert.Contains(response.Status, new[] { 200, 503 });
        Assert.Contains(SpanishText.Unavailable, response.Text, StringComparison.OrdinalIgnoreCase);
        WebFlows.AssertNoStackTrace(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        Assert.DoesNotContain(NewUserPassword, response.Body);
        Assert.Contains(host.Api.Calls.Skip(callsBefore), c => c.Method != HttpMethod.Get);
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");

        host.Api.Intercept = null;
        (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, target.UserId));
    }

    // ---- helpers ----

    /// <summary>The form of one action for a member who holds UsersView: create-user, grant UsersManage (not held), revoke UsersView (held).</summary>
    private static FormModel ActionForm(PageResponse usersPage, string action, Guid targetUserId, string newUserEmail) => action switch
    {
        "create" => FormModel.From(usersPage, usersPage.FindPasswordForm()).Set("email", newUserEmail).Set("password", NewUserPassword),
        "grant" => FormModel.From(usersPage, usersPage.FindPermissionForm("grant", targetUserId, "UsersManage")),
        "revoke" => FormModel.From(usersPage, usersPage.FindPermissionForm("revoke", targetUserId, "UsersView")),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    private static void AssertNoErrorShown(PageResponse page)
    {
        page.ShouldBe(HttpStatusCode.OK);
        Assert.DoesNotContain(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        WebFlows.AssertNoStackTrace(page);
    }

    /// <summary>
    /// A forged or odd submission ends in a controlled outcome: no server error from the Web or from any API call it made,
    /// no leak, the caller still signed in, and the users list still renders with the rows that must be there.
    /// </summary>
    private static async Task AssertControlledAsync(WebTestHost host, WebSession session, PageResponse response, int callsBefore, params Guid[] rowsThatMustRemain)
    {
        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Status is null || (int)c.Status >= 500);

        var users = await WebFlows.UsersAsync(session);

        var listed = WebFlows.ListedUserIds(users);
        Assert.All(rowsThatMustRemain, id => Assert.Contains(id, listed));
        Assert.True(session.HasAuthCookie);
    }

    private static async Task<PageResponse> SubmitCreateUserAsync(WebSession session, string email, string password)
    {
        var page = await WebFlows.UsersAsync(session);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", password);
        return await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));
    }

    private static async Task<PageResponse> SubmitPermissionFormAsync(WebSession session, string action, Guid userId, string permission)
    {
        var page = await WebFlows.UsersAsync(session);
        var form = FormModel.From(page, page.FindPermissionForm(action, userId, permission));
        return await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));
    }

    private static IEnumerable<AngleSharp.Html.Dom.IHtmlFormElement> ActionForms(PageResponse page)
        => page.UsableForms().Where(f => !(f.GetAttribute("action") ?? string.Empty).EndsWith("/logout", StringComparison.OrdinalIgnoreCase));

    private static bool IsHidden(IElement element)
        => element.Closest("[hidden]") is not null;

    /// <summary>The text a person sees inside <paramref name="root"/>: whitespace-collapsed, without anything under a hidden element.</summary>
    private static string VisibleText(IElement root)
    {
        var text = new System.Text.StringBuilder();
        void Walk(INode node)
        {
            if (node is IElement { LocalName: "script" or "style" } || node is IElement e && e.HasAttribute("hidden"))
            {
                return;
            }

            if (node.NodeType == NodeType.Text)
            {
                text.Append(node.TextContent).Append(' ');
            }

            foreach (var child in node.ChildNodes)
            {
                Walk(child);
            }
        }

        Walk(root);
        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }

    private static async Task<List<Conversia.Application.DTOs.Users.UserSummaryResponse>> ListApiUsersAsync(HttpClient client)
        => (await client.GetFromJsonAsync<List<Conversia.Application.DTOs.Users.UserSummaryResponse>>("/api/users"))!;
}
