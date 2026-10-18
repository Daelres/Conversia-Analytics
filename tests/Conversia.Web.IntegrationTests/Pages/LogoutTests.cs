extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>POST /logout (antiforgery-protected); GET /logout is not an action.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class LogoutTests(TestApiFactory api)
{
    [Fact]
    public async Task Post_logout_clears_the_cookie_and_redirects_to_login_or_home_without_calling_the_api()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(home, home.FindLogoutForm());
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        Assert.True(response.IsRedirect, $"Logout should redirect, got {response.Status}.");
        Assert.Contains(response.RedirectTarget, new[] { "/login", "/" });
        Assert.True(WebFlows.ClearsAuthCookie(response), "Logout must expire the authentication cookie.");
        Assert.False(session.HasAuthCookie);
        Assert.Equal(callsBefore, host.Api.CallCount);
        (await session.GetAsync("/users")).ShouldRedirectToLogin();
    }

    [Fact]
    public async Task Post_logout_without_a_valid_token_is_rejected_and_keeps_the_user_signed_in()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(home, home.FindLogoutForm()).WithoutAntiforgeryToken());

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.False(WebFlows.ClearsAuthCookie(response));
        Assert.True(session.HasAuthCookie);
        (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_logout_does_not_sign_the_user_out()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var response = await session.GetAsync("/logout");

        Assert.False(WebFlows.ClearsAuthCookie(response));
        Assert.True(session.HasAuthCookie);
        (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logging_out_one_user_leaves_another_users_session_untouched()
    {
        var first = await ApiSeed.RegisterOwnerAsync(api, "first");
        var second = await ApiSeed.RegisterOwnerAsync(api, "second");
        using var host = WebTestHost.Create(api);
        using var firstSession = await WebFlows.SignedInAsync(host, first.Email);
        using var secondSession = await WebFlows.SignedInAsync(host, second.Email);
        var home = (await firstSession.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        await firstSession.SubmitAsync(FormModel.From(home, home.FindLogoutForm()));

        (await firstSession.GetAsync("/users")).ShouldRedirectToLogin();
        var stillIn = await WebFlows.UsersAsync(secondSession);
        Assert.Contains(second.Email, stillIn.Text);
    }
}
