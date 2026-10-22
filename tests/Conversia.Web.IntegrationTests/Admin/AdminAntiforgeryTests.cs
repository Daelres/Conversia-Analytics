extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>Every POST of the administration needs a valid antiforgery token: without one it is a 400 and changes nothing, and the administrator API is never called.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class AdminAntiforgeryTests(TestApiFactory api)
{
    public static TheoryData<string> Tampering => new() { "missing", "garbage", "other-session" };

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task The_administrators_login_with_a_bad_token_is_refused_before_the_api_is_called(string tampering)
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        var page = (await session.GetAsync("/admin/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", admin.Email).Set("password", admin.Password);
        await TamperAsync(form, tampering, host);

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Every_form_of_a_signed_in_administrator_refuses_a_bad_token_and_changes_nothing(string tampering)
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería");
        await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 1);
        var line = (await ApiSeed.CreateLinesAsync(api, owner, "+57 300 666 0001"))[0];
        await ApiSeed.ActivateLineAsync(api, owner, line);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var company = $"/admin/empresas/{owner.TenantId}";
        var detail = (await session.GetAsync(company)).ShouldBe(HttpStatusCode.OK);
        var suspend = (await session.GetAsync($"{company}/estado/suspender")).ShouldBe(HttpStatusCode.OK);
        var off = (await session.GetAsync($"{company}/lineas/{line}/desactivar")).ShouldBe(HttpStatusCode.OK);

        var forms = new List<FormModel>
        {
            FormModel.From(detail, detail.FindForm(f => f.GetAttribute("data-testid") == "quota-form", "quota form")).Set("MaxActiveLines", "9"),
            FormModel.From(suspend, suspend.FindForm(f => f.GetAttribute("data-testid") == "status-action-form", "status form")),
            FormModel.From(off, off.FindForm(f => f.GetAttribute("data-testid") == "line-action-form", "line form")),
            FormModel.From(detail, detail.FindLogoutForm()),
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
        using var client = await ApiSeed.AdminClientAsync(api, admin);
        var after = (await client.GetFromJsonAsync<TenantDetailResponse>($"/api/admin/tenants/{owner.TenantId}"))!;
        Assert.Equal(Conversia.Domain.Enums.TenantStatus.Active, after.Status);
        Assert.Equal(1, after.MaxActiveLines);
        Assert.True(Assert.Single(after.Lines).IsActive);
        (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK); // the session survived
    }

    [Fact]
    public async Task A_company_users_token_does_not_pass_on_an_administrator_form()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var company = await WebFlows.SignedInAsync(host, owner.Email);
        var companyToken = await WebFlows.AntiforgeryTokenAsync(company);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var detail = (await session.GetAsync($"/admin/empresas/{owner.TenantId}")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(detail, detail.FindForm(f => f.GetAttribute("data-testid") == "quota-form", "quota form")).Set("MaxActiveLines", "9").WithAntiforgeryToken(companyToken);
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal(callsBefore, host.Api.CallCount);
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
                    var page = (await other.GetAsync("/admin/login")).ShouldBe(HttpStatusCode.OK);
                    form.WithAntiforgeryToken(page.AntiforgeryToken()!);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tampering), tampering, null);
        }
    }
}
