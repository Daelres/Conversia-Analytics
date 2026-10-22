using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Domain.Enums;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Services;

/// <summary>The API client's new outcomes: the 403 of a company that cannot operate, the message of a 409, and the calls of the administration. Driven through a stub handler.</summary>
public class ApiClientTenantTests
{
    private static readonly Uri ApiBase = new("http://api.test/");
    private static readonly Guid Id = Guid.Parse("a1f0c2d4-0000-4000-8000-000000000001");

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Authorization, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static ApiClient Client(Stub stub) => new(new HttpClient(stub) { BaseAddress = ApiBase });

    private static HttpResponseMessage Raw(HttpStatusCode status, string? body, string contentType = "application/problem+json")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return response;
    }

    private static HttpResponseMessage Json(object value) => Raw(HttpStatusCode.OK, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json");

    // ---- 403 ----

    [Theory]
    [InlineData("""{"status":403,"code":"tenant_not_active","tenantStatus":"Suspended"}""", "Suspended")]
    [InlineData("""{"status":403,"code":"tenant_not_active"}""", null)]
    [InlineData("""{"status":403,"code":"tenant_not_active","tenantStatus":7}""", null)]
    public async Task A_403_with_the_tenant_not_active_code_is_its_own_outcome(string body, string? tenantStatus)
    {
        var result = await Client(new Stub(_ => Raw(HttpStatusCode.Forbidden, body))).GetBranchesAsync("tok");

        Assert.Equal(ApiResultStatus.TenantNotActive, result.Status);
        Assert.Equal(tenantStatus, result.TenantStatus);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ErrorCode); // it is not an error: nothing is reported
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>no</html>")]
    [InlineData("""{"status":403}""")]
    [InlineData("""{"status":403,"code":"something_else"}""")]
    [InlineData("""{"status":403,"code":"TENANT_NOT_ACTIVE"}""")]
    [InlineData("""{"status":403,"code":7}""")]
    [InlineData("[]")]
    public async Task Any_other_403_is_still_a_plain_refusal(string? body)
    {
        var result = await Client(new Stub(_ => Raw(HttpStatusCode.Forbidden, body))).GetBranchesAsync("tok");

        Assert.Equal(ApiResultStatus.Forbidden, result.Status);
    }

    // ---- 409 ----

    [Fact]
    public async Task A_409_carries_the_apis_message()
    {
        const string message = "Tu cupo de líneas activas está completo (2 de 2). Contacta al administrador de Conversia.";
        var result = await Client(new Stub(_ => Raw(HttpStatusCode.Conflict, $$"""{"status":409,"title":"x","detail":"{{message}}"}"""))).SetChannelActiveAsync("tok", Id, true);

        Assert.Equal(ApiResultStatus.Conflict, result.Status);
        Assert.Equal(message, result.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>conflict</html>")]
    [InlineData("""{"status":409}""")]
    [InlineData("""{"status":409,"detail":""}""")]
    [InlineData("""{"status":409,"detail":"   "}""")]
    [InlineData("""{"status":409,"detail":7}""")]
    public async Task A_409_without_a_usable_message_has_none_and_still_is_a_conflict(string? body)
    {
        var result = await Client(new Stub(_ => Raw(HttpStatusCode.Conflict, body))).SetChannelActiveAsync("tok", Id, true);

        Assert.Equal(ApiResultStatus.Conflict, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task A_very_long_message_is_cut()
    {
        var result = await Client(new Stub(_ => Raw(HttpStatusCode.Conflict, $$"""{"detail":"{{new string('x', 5000)}}"}"""))).SetChannelActiveAsync("tok", Id, true);

        Assert.Equal(300, result.Detail!.Length);
    }

    // ---- the calls ----

    [Fact]
    public async Task The_tenant_call_is_a_get_with_the_callers_token()
    {
        var stub = new Stub(_ => Json(new { id = Id, name = "Aurora", status = "Suspended", maxActiveLines = 3, activeLines = 1, registeredLines = 2 }));

        var result = await Client(stub).GetTenantAsync("tok");

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantStatus.Suspended, result.Value!.Status);
        Assert.Equal(3, result.Value.MaxActiveLines);
        var call = Assert.Single(stub.Calls);
        Assert.Equal((HttpMethod.Get, "/api/tenant", "Bearer tok"), (call.Method, call.Uri.AbsolutePath, call.Authorization));
    }

    [Fact]
    public async Task An_empty_tenant_answer_is_not_usable()
    {
        var result = await Client(new Stub(_ => Json(new { name = "" }))).GetTenantAsync("tok");

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.NotNull(result.ErrorCode);
    }

    [Fact]
    public async Task The_administrators_login_posts_to_its_own_endpoint_without_a_token()
    {
        var stub = new Stub(_ => Json(new { token = "t", adminId = Id, expiresAt = "2030-01-01T00:00:00Z" }));

        var result = await Client(stub).AdminLoginAsync(new LoginRequest { Email = "a@x.test", Password = "p" });

        Assert.Equal(Id, result.Value!.AdminId);
        var call = Assert.Single(stub.Calls);
        Assert.Equal((HttpMethod.Post, "/api/admin/auth/login", (string?)null), (call.Method, call.Uri.AbsolutePath, call.Authorization));
        Assert.Equal(ApiResultStatus.Unauthorized, (await Client(new Stub(_ => Raw(HttpStatusCode.Unauthorized, null))).AdminLoginAsync(new LoginRequest())).Status);
        Assert.Equal(ApiResultStatus.Unavailable, (await Client(new Stub(_ => Json(new { token = "" }))).AdminLoginAsync(new LoginRequest())).Status);
    }

    [Fact]
    public async Task The_administrators_calls_go_to_the_admin_paths_with_the_administrators_token()
    {
        var stub = new Stub(request => request.RequestUri!.AbsolutePath.EndsWith("/deactivate", StringComparison.Ordinal)
            ? Json(new { channelId = Id, displayPhoneNumber = "+57", isActive = false })
            : request.Method == HttpMethod.Put
                ? Json(new { id = Id, name = "A", status = "Active" })
                : request.RequestUri.AbsolutePath.EndsWith("/audit", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.EndsWith("/tenants", StringComparison.Ordinal)
                    ? Json(new { items = Array.Empty<object>(), page = 1, pageSize = 20, totalCount = 0 })
                    : Json(new { id = Id, name = "A", status = "Active", lines = Array.Empty<object>(), recentAudit = Array.Empty<object>() }));
        var client = Client(stub);

        Assert.True((await client.GetAdminTenantsAsync("adm", new AdminTenantsQuery { Status = "Pending", Search = "x y" })).IsSuccess);
        Assert.True((await client.GetAdminTenantAsync("adm", Id)).IsSuccess);
        Assert.True((await client.ChangeTenantStatusAsync("adm", Id, new ChangeTenantStatusRequest { Status = "Active", Reason = "r" })).IsSuccess);
        Assert.True((await client.SetTenantLineQuotaAsync("adm", Id, new SetLineQuotaRequest { MaxActiveLines = 3 })).IsSuccess);
        Assert.True((await client.DeactivateTenantLineAsync("adm", Id, Id)).IsSuccess);
        Assert.True((await client.GetAdminAuditAsync("adm", new AdminAuditQuery { TenantId = Id })).IsSuccess);

        Assert.All(stub.Calls, call => Assert.Equal("Bearer adm", call.Authorization));
        Assert.Equal(
            [
                "GET /api/admin/tenants?status=Pending&search=x%20y&page=1&pageSize=20",
                $"GET /api/admin/tenants/{Id}",
                $"PUT /api/admin/tenants/{Id}/status",
                $"PUT /api/admin/tenants/{Id}/line-quota",
                $"POST /api/admin/tenants/{Id}/channels/{Id}/deactivate",
                $"GET /api/admin/audit?tenantId={Id}&page=1&pageSize=20",
            ],
            stub.Calls.Select(c => $"{c.Method} {c.Uri.PathAndQuery}"));
        using var status = JsonDocument.Parse(stub.Calls[2].Body!);
        Assert.Equal("Active", status.RootElement.GetProperty("status").GetString());
        Assert.Equal("r", status.RootElement.GetProperty("reason").GetString());
        using var quota = JsonDocument.Parse(stub.Calls[3].Body!);
        Assert.Equal(3, quota.RootElement.GetProperty("maxActiveLines").GetInt32());
    }

    [Fact]
    public async Task An_unknown_company_and_a_stale_administrator_are_plain_outcomes()
    {
        Assert.Equal(ApiResultStatus.NotFound, (await Client(new Stub(_ => Raw(HttpStatusCode.NotFound, null))).GetAdminTenantAsync("adm", Id)).Status);
        Assert.Equal(ApiResultStatus.Unauthorized, (await Client(new Stub(_ => Raw(HttpStatusCode.Unauthorized, null))).GetAdminTenantAsync("adm", Id)).Status);
        var conflict = await Client(new Stub(_ => Raw(HttpStatusCode.Conflict, """{"detail":"No se puede bajar el cupo por debajo de las líneas activas: la empresa tiene 2 activa(s). Desactiva líneas primero."}"""))).SetTenantLineQuotaAsync("adm", Id, new SetLineQuotaRequest { MaxActiveLines = 0 });
        Assert.Equal(ApiResultStatus.Conflict, conflict.Status);
        Assert.Contains("2 activa(s)", conflict.Detail);
    }
}
