using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Services;

/// <summary>
/// The Web's API client maps every API answer to an <see cref="ApiResult{T}"/> and never throws for
/// a backend problem. Driven through a stub handler; no host, no database.
/// </summary>
public partial class ApiClientTests
{
    private static readonly Uri ApiBase = new("http://api.test/");
    private static readonly Guid UserId = Guid.Parse("5b0d0b5e-39a5-4d88-8a2f-3d9b2f0d7e11");
    private static readonly Guid TenantId = Guid.Parse("a1f0c2d4-0000-4000-8000-000000000001");

    // ---- login / register: success and request shape ----

    [Fact]
    public async Task Login_success_returns_the_auth_response_and_posts_credentials_as_json()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new { token = "tok-1", userId = UserId, tenantId = TenantId, isOwner = true }));

        var result = await Client(stub).LoginAsync(new LoginRequest { Email = "a@example.test", Password = "pw-value-1" });

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal("tok-1", result.Value!.Token);
        Assert.Equal(UserId, result.Value.UserId);
        Assert.Equal(TenantId, result.Value.TenantId);
        Assert.True(result.Value.IsOwner);

        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/auth/login", call.Uri.AbsolutePath);
        Assert.Null(call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("a@example.test", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("pw-value-1", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task RegisterTenant_201_returns_the_auth_response_and_posts_the_registration_as_json()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.Created, new { token = "tok-2", userId = UserId, tenantId = TenantId, isOwner = true }));

        var result = await Client(stub).RegisterTenantAsync(new RegisterTenantRequest { TenantName = "Acme", Email = "o@example.test", Password = "Sup3r-Secret!" });

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal("tok-2", result.Value!.Token);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/auth/register-tenant", call.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("Acme", body.RootElement.GetProperty("tenantName").GetString());
        Assert.Equal("o@example.test", body.RootElement.GetProperty("email").GetString());
    }

    // ---- status mapping (authorized calls and registration; login has its own, narrower table) ----

    public static TheoryData<int, ApiResultStatus> StatusMapping => new()
    {
        { 401, ApiResultStatus.Unauthorized },
        { 403, ApiResultStatus.Forbidden },
        { 404, ApiResultStatus.NotFound },
        { 409, ApiResultStatus.Conflict },
        { 500, ApiResultStatus.Unavailable },
        { 502, ApiResultStatus.Unavailable },
        { 503, ApiResultStatus.Unavailable },
        { 504, ApiResultStatus.Unavailable },
        { 418, ApiResultStatus.Unavailable },
    };

    /// <summary>What the login endpoint can actually answer with besides 200: 400 (validation), 401, 429 and server errors.</summary>
    public static TheoryData<int, ApiResultStatus> LoginStatusMapping => new()
    {
        { 400, ApiResultStatus.ValidationFailed },
        { 401, ApiResultStatus.Unauthorized },
        { 429, ApiResultStatus.RateLimited },
        { 500, ApiResultStatus.Unavailable },
        { 502, ApiResultStatus.Unavailable },
        { 503, ApiResultStatus.Unavailable },
        { 504, ApiResultStatus.Unavailable },
    };

    [Theory]
    [MemberData(nameof(LoginStatusMapping))]
    public async Task Login_maps_the_statuses_the_api_can_answer_with(int status, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, status == 400
            ? """{"title":"One or more validation errors occurred.","status":400,"errors":{"Email":["Bad email."]}}"""
            : """{"title":"x","status":0}"""));

        var result = await Client(stub).LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Theory]
    [MemberData(nameof(StatusMapping))]
    public async Task Authorized_reads_map_error_statuses(int status, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, """{"title":"x","status":0}"""));
        var client = Client(stub);

        AssertStatus(status, "GetUsers", expected, (await client.GetUsersAsync("t")).Status);
        AssertStatus(status, "GetMyProfile", expected, (await client.GetMyProfileAsync("t")).Status);
    }

    [Theory]
    [MemberData(nameof(StatusMapping))]
    public async Task Writes_and_registration_map_error_statuses(int status, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, """{"title":"x","status":0}"""));
        var client = Client(stub);

        AssertStatus(status, "CreateUser", expected, (await client.CreateUserAsync("t", new CreateUserRequest { Email = "n@example.test", Password = "Sup3r-Secret!" })).Status);
        AssertStatus(status, "AssignPermission", expected, (await client.AssignPermissionAsync("t", UserId, Permission.UsersView)).Status);
        AssertStatus(status, "RevokePermission", expected, (await client.RevokePermissionAsync("t", UserId, Permission.UsersView)).Status);
        AssertStatus(status, "RegisterTenant", expected, (await client.RegisterTenantAsync(new RegisterTenantRequest { TenantName = "T", Email = "n@example.test", Password = "Sup3r-Secret!" })).Status);
    }

    [Fact]
    public async Task Validation_problem_400_exposes_the_per_field_messages()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.BadRequest, """
            {"title":"One or more validation errors occurred.","status":400,
             "errors":{"Password":["Password must be 12-128 characters.","Needs a digit."],"Email":["Bad email."]}}
            """));

        var result = await Client(stub).RegisterTenantAsync(new RegisterTenantRequest { TenantName = "T", Email = "x", Password = "p" });

        Assert.Equal(ApiResultStatus.ValidationFailed, result.Status);
        Assert.Equal(new[] { "Password must be 12-128 characters.", "Needs a digit." }, result.ValidationErrors["Password"]);
        Assert.Equal(new[] { "Bad email." }, result.ValidationErrors["Email"]);
    }

    [Fact]
    public async Task Rate_limited_429_carries_the_retry_after_seconds()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, """{"title":"Too Many Requests","status":429}""", retryAfter: "42"));

        var result = await Client(stub).LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.Equal(ApiResultStatus.RateLimited, result.Status);
        Assert.Equal(42, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Rate_limited_429_without_a_usable_retry_after_is_still_rate_limited()
    {
        var withoutHeader = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, """{"status":429}"""));
        var garbage = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, """{"status":429}""", retryAfter: "soon"));

        var first = await Client(withoutHeader).LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });
        var second = await Client(garbage).LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.True(first.Status == ApiResultStatus.RateLimited, $"Login without a Retry-After header: expected RateLimited but got {first.Status}.");
        Assert.True(first.RetryAfterSeconds is null, $"Login without a Retry-After header: expected no seconds but got {first.RetryAfterSeconds}.");
        Assert.True(second.Status == ApiResultStatus.RateLimited, $"Login with Retry-After 'soon': expected RateLimited but got {second.Status}.");
        Assert.True(second.RetryAfterSeconds is null, $"Login with Retry-After 'soon': expected no seconds but got {second.RetryAfterSeconds}.");
    }

    // ---- backend misbehaviour never throws ----

    public static TheoryData<string> BrokenBackends => new()
    {
        "connection refused",
        "timeout",
        "html 200",
        "empty 200",
        "json null 200",
        "truncated json 200",
        "html 400",
    };

    [Theory]
    [MemberData(nameof(BrokenBackends))]
    public async Task Login_degrades_to_Unavailable_when_the_backend_misbehaves(string failure)
    {
        var client = Client(BrokenStub(failure));

        var result = await client.LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [MemberData(nameof(BrokenBackends))]
    public async Task Authorized_calls_degrade_to_Unavailable_when_the_backend_misbehaves(string failure)
    {
        var client = Client(BrokenStub(failure));

        AssertStatus(failure, "GetUsers", ApiResultStatus.Unavailable, (await client.GetUsersAsync("t")).Status);
        AssertStatus(failure, "GetMyProfile", ApiResultStatus.Unavailable, (await client.GetMyProfileAsync("t")).Status);
        AssertStatus(failure, "CreateUser", ApiResultStatus.Unavailable, (await client.CreateUserAsync("t", new CreateUserRequest { Email = "n@example.test", Password = "Sup3r-Secret!" })).Status);
    }

    // ---- authorized calls: URL, verb, token per call ----

    [Fact]
    public async Task GetMyProfile_sends_the_bearer_token_and_maps_the_profile()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new { userId = UserId, tenantId = TenantId, isOwner = false, permissions = new[] { 1, 2 } }));

        var result = await Client(stub).GetMyProfileAsync("my-token");

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal(TenantId, result.Value!.TenantId);
        Assert.False(result.Value.IsOwner);
        Assert.Equal(new[] { Permission.UsersView, Permission.UsersManage }, result.Value.Permissions);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("/api/users/me", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
    }

    [Fact]
    public async Task GetUsers_sends_the_bearer_token_and_maps_the_list()
    {
        var other = Guid.NewGuid();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new[]
        {
            new { userId = UserId, email = "o@example.test", isOwner = true },
            new { userId = other, email = "m@example.test", isOwner = false },
        }));

        var result = await Client(stub).GetUsersAsync("my-token");

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Collection(result.Value!,
            first => { Assert.Equal(UserId, first.UserId); Assert.Equal("o@example.test", first.Email); Assert.True(first.IsOwner); },
            second => { Assert.Equal(other, second.UserId); Assert.Equal("m@example.test", second.Email); Assert.False(second.IsOwner); });
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("/api/users", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
    }

    [Fact]
    public async Task CreateUser_201_maps_the_created_user_and_posts_email_and_password()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.Created, new { userId = UserId, email = "n@example.test", isOwner = false }));

        var result = await Client(stub).CreateUserAsync("my-token", new CreateUserRequest { Email = "n@example.test", Password = "Sup3r-Secret!" });

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal(UserId, result.Value!.UserId);
        Assert.Equal("n@example.test", result.Value.Email);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/users", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("n@example.test", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("Sup3r-Secret!", body.RootElement.GetProperty("password").GetString());
    }

    [Theory]
    [InlineData(Permission.UsersView, 1)]
    [InlineData(Permission.UsersManage, 2)]
    public async Task AssignPermission_posts_the_permission_for_the_user(Permission permission, int number)
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.NoContent));

        var result = await Client(stub).AssignPermissionAsync("my-token", UserId, permission);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"/api/users/{UserId}/permissions", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        var sent = body.RootElement.GetProperty("permission");
        var parsed = sent.ValueKind == JsonValueKind.Number ? sent.GetInt32() : (int)Enum.Parse<Permission>(sent.GetString()!, ignoreCase: true);
        Assert.Equal(number, parsed);
    }

    [Theory]
    [InlineData(Permission.UsersView)]
    [InlineData(Permission.UsersManage)]
    public async Task RevokePermission_deletes_the_permission_of_the_user(Permission permission)
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.NoContent));

        var result = await Client(stub).RevokePermissionAsync("my-token", UserId, permission);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.StartsWith($"/api/users/{UserId}/permissions/", call.Uri.AbsolutePath);
        var segment = call.Uri.AbsolutePath[$"/api/users/{UserId}/permissions/".Length..];
        var parsed = int.TryParse(segment, out var number) ? (Permission)number : Enum.Parse<Permission>(segment, ignoreCase: true);
        Assert.Equal(permission, parsed);
        Assert.Equal("Bearer my-token", call.Authorization);
    }

    // ---- no shared token state ----

    [Fact]
    public async Task Concurrent_calls_with_different_tokens_never_mix_them_up()
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var stub = new StubHandler(async request =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothArrived.SetResult();
            }

            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10)); // fails (instead of hanging) if the client serialises calls
            var bearer = request.Headers.Authorization!.Parameter!;
            return await Json(HttpStatusCode.OK, new[] { new { userId = Guid.NewGuid(), email = $"{bearer}@example.test", isOwner = false } });
        });
        var client = Client(stub);

        var results = await Task.WhenAll(client.GetUsersAsync("token-A"), client.GetUsersAsync("token-B")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.Status == ApiResultStatus.Success, $"Both overlapping calls must succeed but one was {r.Status}."));
        Assert.Equal("token-A@example.test", Assert.Single(results[0].Value!).Email);
        Assert.Equal("token-B@example.test", Assert.Single(results[1].Value!).Email);
    }

    [Fact]
    public async Task A_token_used_for_one_call_is_not_sent_on_later_calls()
    {
        var stub = new StubHandler(request => request.RequestUri!.AbsolutePath == "/api/auth/login"
            ? Json(HttpStatusCode.OK, new { token = "t", userId = UserId, tenantId = TenantId, isOwner = true })
            : Json(HttpStatusCode.OK, Array.Empty<object>()));
        var client = Client(stub);

        await client.GetUsersAsync("someone-elses-token");
        await client.LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.Equal("Bearer someone-elses-token", stub.Calls[0].Authorization);
        Assert.Null(stub.Calls[1].Authorization);
    }

    // ---- helpers ----

    /// <summary>Fails with the name of the call and the scenario (HTTP status or failure kind), which a bare Assert.Equal on a loop of calls would not.</summary>
    private static void AssertStatus(object scenario, string call, ApiResultStatus expected, ApiResultStatus actual)
        => Assert.True(expected == actual, $"{call} on '{scenario}': expected {expected} but got {actual}.");

    private static ApiClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = ApiBase });

    private static StubHandler BrokenStub(string failure) => failure switch
    {
        "connection refused" => new StubHandler(_ => throw new HttpRequestException("connection refused")),
        "timeout" => new StubHandler(_ => throw new TaskCanceledException("timed out", new TimeoutException())),
        "html 200" => new StubHandler(_ => Respond(HttpStatusCode.OK, "<html><body>oops</body></html>", "text/html")),
        "empty 200" => new StubHandler(_ => Respond(HttpStatusCode.OK)),
        "json null 200" => new StubHandler(_ => Respond(HttpStatusCode.OK, "null", "application/json")),
        "truncated json 200" => new StubHandler(_ => Respond(HttpStatusCode.OK, """{"token":"abc","userI""", "application/json")),
        "html 400" => new StubHandler(_ => Respond(HttpStatusCode.BadRequest, "<html>bad</html>", "text/html")),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
    };

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, object value)
        => Respond(status, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json");

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string? body = null, string contentType = "application/problem+json", string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return Task.FromResult(response);
    }

    private sealed record RecordedCall(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        private readonly List<RecordedCall> _calls = new();

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_calls)
            {
                _calls.Add(new RecordedCall(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            }

            return await _respond(request);
        }
    }
}
