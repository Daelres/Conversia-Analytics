extern alias ApiHost;

using System.Net;
using System.Text;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Security;

/// <summary>Every POST needs a valid antiforgery token; without one it is a 400 with no side effect and no API call.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class AntiforgeryTests(TestApiFactory api)
{
    public static TheoryData<string> Tampering => new() { "missing", "garbage", "other-session" };

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Login_post_with_a_bad_token_is_rejected_before_the_api_is_called(string tampering)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", owner.Email).Set("password", ApiSeed.StrongPassword);
        await TamperAsync(form, tampering, host);

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
        WebFlows.AssertNoStackTrace(response);
    }

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Register_post_with_a_bad_token_is_rejected_before_the_api_is_called(string tampering)
    {
        var tenantName = ApiSeed.UniqueTenantName("csrf");
        var email = ApiSeed.UniqueEmail("csrf");
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        var page = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm())
            .Set("tenantName", tenantName).Set("email", email).Set("password", ApiSeed.StrongPassword);
        await TamperAsync(form, tampering, host);

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
        Assert.Equal(0, await ApiSeed.CountTenantsNamedAsync(api, tenantName));
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
    }

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Authenticated_forms_with_a_bad_token_are_rejected_and_change_nothing(string tampering)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "target", Permission.UsersView);
        var email = ApiSeed.UniqueEmail("csrf-user");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = await WebFlows.UsersAsync(session);
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var forms = new List<FormModel>
        {
            FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", ApiSeed.StrongPassword),
            FormModel.From(page, page.FindPermissionForm("grant", target.UserId, "UsersManage")),
            FormModel.From(page, page.FindPermissionForm("revoke", target.UserId, "UsersView")),
            FormModel.From(home, home.FindLogoutForm()),
        };
        var callsBefore = host.Api.CallCount;

        foreach (var form in forms)
        {
            await TamperAsync(form, tampering, host);
            var response = await session.SubmitAsync(form);

            response.ShouldBe(HttpStatusCode.BadRequest);
            Assert.False(WebFlows.ClearsAuthCookie(response));
        }

        Assert.Equal(callsBefore, host.Api.CallCount);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        Assert.Equal(new[] { Permission.UsersView }, await ApiSeed.PersistedPermissionsAsync(api, target.UserId));
        Assert.True(session.HasAuthCookie);
        (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_from_another_signed_in_user_is_rejected()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "victim");
        var attacker = await ApiSeed.RegisterOwnerAsync(api, "attacker");
        var email = ApiSeed.UniqueEmail("csrf-cross-user");
        using var host = WebTestHost.Create(api);
        using var victimSession = await WebFlows.SignedInAsync(host, owner.Email);
        using var attackerSession = await WebFlows.SignedInAsync(host, attacker.Email);
        var attackerToken = await WebFlows.AntiforgeryTokenAsync(attackerSession);
        var page = await WebFlows.UsersAsync(victimSession);
        var form = FormModel.From(page, page.FindPasswordForm())
            .Set("email", email).Set("password", ApiSeed.StrongPassword).WithAntiforgeryToken(attackerToken);
        var callsBefore = host.Api.CallCount;

        var response = await victimSession.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal(callsBefore, host.Api.CallCount);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    public async Task Posting_a_non_form_body_is_rejected_without_calling_the_api(string path)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);
        var json = new StringContent($$"""{"email":"{{owner.Email}}","password":"{{ApiSeed.StrongPassword}}"}""", Encoding.UTF8, "application/json");

        var response = await session.PostRawAsync(path, json);

        Assert.InRange((int)response.StatusCode, 400, 499);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
        WebFlows.AssertNoStackTrace(response);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    public async Task Posting_an_empty_form_is_rejected_without_calling_the_api(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var response = await session.PostRawAsync(path, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
    }

    private static async Task TamperAsync(FormModel form, string tampering, WebTestHost host)
    {
        switch (tampering)
        {
            case "missing":
                form.WithoutAntiforgeryToken();
                break;
            case "garbage":
                form.WithAntiforgeryToken("CfDJ8-this-is-not-a-valid-token");
                break;
            case "other-session":
                using (var other = host.NewSession())
                {
                    var page = (await other.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
                    form.WithAntiforgeryToken(page.AntiforgeryToken()!);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tampering), tampering, null);
        }
    }
}
