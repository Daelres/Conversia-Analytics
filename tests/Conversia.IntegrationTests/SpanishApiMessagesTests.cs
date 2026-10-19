using System.Net;
using System.Net.Http.Json;
using System.Text;
using Conversia.Application.DTOs;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Requirement "all text shown to users is Spanish", API side: the <c>title</c>, <c>detail</c> and every validation message of every
/// problem+json answer, including the ones the frameworks write when nobody localised them (malformed JSON, a missing body,
/// wrong value types, unknown enum names, bad query values). Answers are collected by <see cref="ApiMessageReport"/>, so a
/// failing test lists every English message it met. The fixed texts of the requirement are asserted exactly in the first group.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class SpanishApiMessagesTests
{
    private const string RegisterUrl = "/api/auth/register-tenant";
    private const string LoginUrl = "/api/auth/login";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SpanishApiMessagesTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- the texts the requirement fixes, exactly ----

    [Fact]
    public async Task Wrong_credentials_and_unknown_email_answer_401_with_the_same_spanish_detail()
    {
        var owner = await RegisterTenantSessionAsync(_client);

        using var wrongPassword = await _client.PostAsJsonAsync(LoginUrl, new { email = owner.OwnerEmail, password = "Wr0ng-Pass-Value-123" });
        using var unknownEmail = await _client.PostAsJsonAsync(LoginUrl, new { email = UniqueEmail("nadie"), password = StrongPassword });

        foreach (var response in new[] { wrongPassword, unknownEmail })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ApiMessages.InvalidCredentials, (await ReadProblemJsonAsync(response)).Detail);
        }

        Assert.Equal(ApiMessages.InvalidCredentials, new InvalidCredentialsException().Message);
    }

    [Fact]
    public async Task A_duplicate_email_answers_409_with_the_spanish_detail_on_register_and_on_create_user()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);

        using var register = await _client.PostAsJsonAsync(RegisterUrl, new { tenantName = UniqueTenantName("Duplicada"), email = owner.OwnerEmail, password = StrongPassword });
        using var create = await CreateUserAsync(ownerClient, owner.OwnerEmail);

        foreach (var response in new[] { register, create })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ApiMessages.DuplicateEmail, (await ReadProblemJsonAsync(response)).Detail);
        }

        Assert.Equal(ApiMessages.DuplicateEmail, new DuplicateEmailException().Message);
    }

    [Fact]
    public async Task An_unknown_branch_answers_404_with_the_spanish_detail()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);

        using var get = await ownerClient.GetAsync($"{BranchesUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(ApiMessages.BranchNotFound, (await ReadProblemJsonAsync(get)).Detail);
        Assert.Equal(ApiMessages.BranchNotFound, new BranchNotFoundException().Message);
    }

    [Fact]
    public async Task An_unknown_user_answers_404_with_the_spanish_detail_on_grant_and_revoke()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var unknown = Guid.NewGuid();

        using var grant = await ownerClient.PostAsJsonAsync($"/api/users/{unknown}/permissions", new { permission = (int)Permission.UsersView });
        using var revoke = await RevokeAsync(ownerClient, unknown, Permission.UsersView);

        foreach (var response in new[] { grant, revoke })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ApiMessages.UserNotFound, (await ReadProblemJsonAsync(response)).Detail);
        }

        Assert.Equal(ApiMessages.UserNotFound, new UserNotFoundException().Message);
    }

    [Fact]
    public async Task A_duplicate_branch_name_answers_409_with_the_spanish_detail()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var first = await CreateBranchAsync(ownerClient, "Sede Centro");

        using var second = await PostBranchAsync(ownerClient, first.Name.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ApiMessages.DuplicateBranchName, (await ReadProblemJsonAsync(second)).Detail);
        Assert.Equal(ApiMessages.DuplicateBranchName, new DuplicateBranchNameException().Message);
    }

    [Fact]
    public async Task The_login_rate_limit_answers_429_with_the_spanish_detail()
    {
        var limited = _factory.WithLoginRateLimit(1, 3600);
        using var client = RateLimitTestSupport.NewClient(limited);
        using (await RateLimitTestSupport.MalformedLogin(client)) // spends the single permit
        {
        }

        using var throttled = await RateLimitTestSupport.MalformedLogin(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var problem = await ReadProblemJsonAsync(throttled);
        Assert.Equal(ApiMessages.RateLimited, problem.Detail);
        var report = new ApiMessageReport();
        await report.CheckAsync("429", throttled, HttpStatusCode.TooManyRequests);
        report.AssertClean();
    }

    [Fact]
    public async Task An_unexpected_failure_answers_500_with_the_spanish_detail_and_leaks_nothing()
    {
        const string secret = "sql-connection-password-in-the-exception";
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IAuthService)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddScoped<IAuthService>(_ => new ThrowingAuthService(secret));
        }));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(LoginUrl, new { email = UniqueEmail("boom"), password = StrongPassword });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body);
        TestHelpers.AssertNoInternalsLeaked(body);
        Assert.Equal(ApiMessages.Unexpected, (await ReadProblemJsonAsync(response)).Detail);
        Assert.Matches(ErrorContract.CodePattern, Assert.Single(response.Headers.GetValues(ErrorContract.HeaderName)));
        var report = new ApiMessageReport();
        await report.CheckAsync("500", response, HttpStatusCode.InternalServerError);
        report.AssertClean();
    }

    [Fact]
    public async Task Every_validation_answer_is_titled_in_spanish()
    {
        using var response = await _client.PostAsJsonAsync(LoginUrl, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        Assert.Equal(ApiMessages.ValidationTitle, problem!.Title);
    }

    // ---- title and detail of every handled error are Spanish ----

    [Fact]
    public async Task Every_handled_error_has_a_spanish_title_and_detail()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var other = await RegisterTenantSessionAsync(_client, "otro");
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var branch = await CreateBranchAsync(ownerClient, "Sede Centro");
        var report = new ApiMessageReport();

        await report.CheckAsync("login wrong password", await _client.PostAsJsonAsync(LoginUrl, new { email = owner.OwnerEmail, password = "Wr0ng-Pass-Value-123" }), HttpStatusCode.Unauthorized);
        await report.CheckAsync("login unknown email", await _client.PostAsJsonAsync(LoginUrl, new { email = UniqueEmail("nadie"), password = StrongPassword }), HttpStatusCode.Unauthorized);
        await report.CheckAsync("register duplicate email", await _client.PostAsJsonAsync(RegisterUrl, new { tenantName = UniqueTenantName("Duplicada"), email = owner.OwnerEmail, password = StrongPassword }), HttpStatusCode.Conflict);
        await report.CheckAsync("create user duplicate email (other tenant)", await CreateUserAsync(ownerClient, other.OwnerEmail), HttpStatusCode.Conflict);
        await report.CheckAsync("branch get unknown", await ownerClient.GetAsync($"{BranchesUrl}/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await report.CheckAsync("branch of another tenant", await AuthorizedClient(_factory, other.OwnerToken).GetAsync($"{BranchesUrl}/{branch.Id}"), HttpStatusCode.NotFound);
        await report.CheckAsync("branch duplicate name", await PostBranchAsync(ownerClient, branch.Name), HttpStatusCode.Conflict);
        await report.CheckAsync("conversation get unknown", await ownerClient.GetAsync($"/api/conversations/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await report.CheckAsync("grant unknown user", await ownerClient.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/permissions", new { permission = (int)Permission.UsersView }), HttpStatusCode.NotFound);
        await report.CheckAsync("grant user of another tenant", await ownerClient.PostAsJsonAsync($"/api/users/{other.OwnerId}/permissions", new { permission = (int)Permission.UsersView }), HttpStatusCode.NotFound);
        await report.CheckAsync("revoke unknown user", await RevokeAsync(ownerClient, Guid.NewGuid(), Permission.UsersManage), HttpStatusCode.NotFound);

        var limited = _factory.WithLoginRateLimit(1, 3600);
        using var limitedClient = RateLimitTestSupport.NewClient(limited);
        using (await RateLimitTestSupport.MalformedLogin(limitedClient))
        {
        }

        await report.CheckAsync("login rate limit", await RateLimitTestSupport.MalformedLogin(limitedClient), HttpStatusCode.TooManyRequests);

        report.AssertClean();
    }

    // ---- every validation rule of every request ----

    private static string Long(int length) => new('x', length);

    private async Task<(string OwnerToken, Guid ConversationId, Guid MemberId)> ArrangeAsync()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        var member = await SeedMemberAsync(_factory, _client, owner.TenantId);
        return (owner.OwnerToken, Guid.NewGuid(), member.UserId);
    }

    [Fact]
    public async Task Every_validation_rule_of_register_login_and_create_user_answers_in_spanish()
    {
        var (token, _, _) = await ArrangeAsync();
        using var ownerClient = AuthorizedClient(_factory, token);
        var report = new ApiMessageReport();

        async Task Bad(string label, string url, HttpClient client, object body)
            => await report.CheckAsync(label, await client.PostAsJsonAsync(url, body), HttpStatusCode.BadRequest, validationProblem: true);

        var weakPasswords = new[]
        {
            ("short", "Abcdefghi1k"), ("no uppercase", "abcdefghijk1"), ("no lowercase", "ABCDEFGHIJK1"), ("no digit", "Abcdefghijkl"),
            ("far too short", "Aa1"), ("whitespace", "            "), ("129 characters", "Aa1" + Long(126)), ("empty", ""),
        };
        foreach (var (reason, password) in weakPasswords)
        {
            await Bad($"register: password {reason}", RegisterUrl, _client, new { tenantName = UniqueTenantName("Debil"), email = UniqueEmail("debil"), password });
            await Bad($"create user: password {reason}", "/api/users", ownerClient, new { email = UniqueEmail("debil"), password });
        }

        await Bad("register: password null", RegisterUrl, _client, new { tenantName = UniqueTenantName("Debil"), email = UniqueEmail("x"), password = (string?)null });
        await Bad("register: password missing", RegisterUrl, _client, new { tenantName = UniqueTenantName("Debil"), email = UniqueEmail("x") });
        await Bad("register: empty object", RegisterUrl, _client, new { });
        foreach (var (reason, tenantName) in new (string, string?)[] { ("empty", ""), ("whitespace", "   "), ("null", null), ("201 characters", Long(201)) })
        {
            await Bad($"register: tenant name {reason}", RegisterUrl, _client, new { tenantName, email = UniqueEmail("x"), password = StrongPassword });
        }

        var badEmails = new (string, string?)[]
        {
            ("no at sign", "missing-at-sign.example.test"), ("no local part", "@example.test"), ("no domain", "member@"), ("not an email", "not-an-email"),
            ("empty", ""), ("whitespace", " "), ("null", null), ("257 characters", Long(244) + "@example.test"),
        };
        foreach (var (reason, email) in badEmails)
        {
            await Bad($"register: email {reason}", RegisterUrl, _client, new { tenantName = UniqueTenantName("Correo"), email, password = StrongPassword });
            await Bad($"login: email {reason}", LoginUrl, _client, new { email, password = StrongPassword });
            await Bad($"create user: email {reason}", "/api/users", ownerClient, new { email, password = StrongPassword });
        }

        await Bad("login: empty object", LoginUrl, _client, new { });
        await Bad("login: password empty", LoginUrl, _client, new { email = UniqueEmail("x"), password = "" });
        await Bad("login: password null", LoginUrl, _client, new { email = UniqueEmail("x"), password = (string?)null });
        await Bad("create user: empty object", "/api/users", ownerClient, new { });

        report.AssertClean();
    }

    [Fact]
    public async Task Every_validation_rule_of_create_branch_and_the_conversation_list_answers_in_spanish()
    {
        var (token, _, _) = await ArrangeAsync();
        using var ownerClient = AuthorizedClient(_factory, token);
        var report = new ApiMessageReport();

        foreach (var (reason, name) in new (string, string?)[] { ("empty", ""), ("whitespace", "   "), ("null", null), ("101 characters", Long(101)), ("NUL character", "Sede\0Centro") })
        {
            await report.CheckAsync($"create branch: name {reason}", await ownerClient.PostAsJsonAsync(BranchesUrl, new { name }), HttpStatusCode.BadRequest, validationProblem: true);
        }

        await report.CheckAsync("create branch: empty object", await ownerClient.PostAsJsonAsync(BranchesUrl, new { }), HttpStatusCode.BadRequest, validationProblem: true);

        foreach (var query in new[]
        {
            "page=0", "page=-1", "page=abc", "page=", "page=99999999999", "page=1.5",
            "pageSize=0", "pageSize=101", "pageSize=-5", "pageSize=abc", "pageSize=", "pageSize=99999999999",
            "stage=Nope", "source=Nope", "status=Nope", "from=not-a-date", "branchId=not-a-guid",
        })
        {
            await report.CheckAsync($"conversations ?{query}", await ownerClient.GetAsync("/api/conversations?" + query), HttpStatusCode.BadRequest, validationProblem: true);
        }

        report.AssertClean();
    }

    [Fact]
    public async Task Every_rule_of_granting_and_revoking_a_permission_answers_in_spanish()
    {
        var (token, _, memberId) = await ArrangeAsync();
        using var ownerClient = AuthorizedClient(_factory, token);
        var report = new ApiMessageReport();
        var grant = $"/api/users/{memberId}/permissions";

        foreach (var (reason, body) in new (string, string)[]
        {
            ("zero", """{"permission":0}"""), ("undefined number", """{"permission":999}"""), ("negative", """{"permission":-1}"""),
            ("unknown name", """{"permission":"NotAPermission"}"""), ("comma list", """{"permission":"UsersView,UsersManage"}"""),
            ("null", """{"permission":null}"""), ("missing", "{}"), ("decimal", """{"permission":1.5}"""),
            ("boolean", """{"permission":true}"""), ("array", """{"permission":["UsersView"]}"""), ("object", """{"permission":{}}"""),
        })
        {
            await report.CheckAsync($"grant: {reason}", await ownerClient.PostAsync(grant, Json(body)), HttpStatusCode.BadRequest, validationProblem: true);
        }

        foreach (var value in new[] { "0", "999", "-1", "NotAPermission", "UsersView,UsersManage", "1,2", "%20" })
        {
            await report.CheckAsync($"revoke: {value}", await ownerClient.DeleteAsync($"/api/users/{memberId}/permissions/{value}"), HttpStatusCode.BadRequest, validationProblem: true);
        }

        report.AssertClean();
    }

    // ---- what the frameworks say when nobody translated it ----

    [Fact]
    public async Task Malformed_json_a_missing_body_and_wrong_value_types_answer_in_spanish()
    {
        var (token, conversationId, memberId) = await ArrangeAsync();
        using var ownerClient = AuthorizedClient(_factory, token);
        var report = new ApiMessageReport();

        var endpoints = new (string Label, HttpMethod Method, string Url, HttpClient Client)[]
        {
            ("login", HttpMethod.Post, LoginUrl, _client),
            ("register", HttpMethod.Post, RegisterUrl, _client),
            ("create user", HttpMethod.Post, "/api/users", ownerClient),
            ("create branch", HttpMethod.Post, BranchesUrl, ownerClient),
            ("assign advisor", HttpMethod.Put, $"/api/conversations/{conversationId}/advisor", ownerClient),
            ("grant permission", HttpMethod.Post, $"/api/users/{memberId}/permissions", ownerClient),
        };
        var badBodies = new (string Label, string Body)[]
        {
            ("not json", "{ this is not valid json "), ("empty body", ""), ("null literal", "null"), ("array", "[]"), ("string", "\"just text\""),
            ("unterminated", """{"email":"a@b.test","password":"""), ("plain text", "hello"), ("trailing garbage", """{"a":1} xyz"""),
        };
        foreach (var endpoint in endpoints)
        {
            foreach (var (label, body) in badBodies)
            {
                using var request = new HttpRequestMessage(endpoint.Method, endpoint.Url) { Content = Json(body) };
                await report.CheckAsync($"{endpoint.Label}: {label}", await endpoint.Client.SendAsync(request), HttpStatusCode.BadRequest, validationProblem: true);
            }
        }

        var wrongTypes = new (string Label, string Url, HttpMethod Method, string Body, HttpClient Client)[]
        {
            ("register: tenantName number", RegisterUrl, HttpMethod.Post, """{"tenantName":123,"email":"a@b.test","password":"Sup3r-Secret!"}""", _client),
            ("register: email object", RegisterUrl, HttpMethod.Post, """{"tenantName":"x","email":{},"password":"Sup3r-Secret!"}""", _client),
            ("login: password number", LoginUrl, HttpMethod.Post, """{"email":"a@b.test","password":123}""", _client),
            ("login: email array", LoginUrl, HttpMethod.Post, """{"email":["a@b.test"],"password":"x"}""", _client),
            ("create branch: name number", BranchesUrl, HttpMethod.Post, """{"name":123}""", ownerClient),
            ("create branch: name array", BranchesUrl, HttpMethod.Post, """{"name":[]}""", ownerClient),
            ("create branch: name object", BranchesUrl, HttpMethod.Post, """{"name":{}}""", ownerClient),
            ("assign advisor: advisorId number", $"/api/conversations/{conversationId}/advisor", HttpMethod.Put, """{"advisorId":123}""", ownerClient),
            ("assign advisor: advisorId not a guid", $"/api/conversations/{conversationId}/advisor", HttpMethod.Put, """{"advisorId":"abc"}""", ownerClient),
            ("grant: permission string", $"/api/users/{memberId}/permissions", HttpMethod.Post, """{"permission":"abc"}""", ownerClient),
            ("grant: permission unknown enum name", $"/api/users/{memberId}/permissions", HttpMethod.Post, """{"permission":"Superuser"}""", ownerClient),
        };
        foreach (var (label, url, method, body, client) in wrongTypes)
        {
            using var request = new HttpRequestMessage(method, url) { Content = Json(body) };
            await report.CheckAsync(label, await client.SendAsync(request), HttpStatusCode.BadRequest, validationProblem: true);
        }

        report.AssertClean();
    }

    [Fact]
    public async Task A_body_with_an_unsupported_content_type_answers_in_spanish()
    {
        var report = new ApiMessageReport();

        using var plain = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = new StringContent("email=a@b.test&password=x", Encoding.UTF8, "text/plain") };
        using var form = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "a@b.test", ["password"] = "x" }) };

        await report.CheckAsync("login: text/plain", await _client.SendAsync(plain), HttpStatusCode.UnsupportedMediaType, requireExplanation: false);
        await report.CheckAsync("login: form encoded", await _client.SendAsync(form), HttpStatusCode.UnsupportedMediaType, requireExplanation: false);

        report.AssertClean();
    }

    // ---- Accept-Language does not change anything: the API answers in Spanish whatever the caller asks for ----

    public static TheoryData<string> OtherLanguages => new() { "en-US", "en-US,en;q=0.9", "fr", "fr-FR,fr;q=0.8,en;q=0.5", "de-DE" };

    private static HttpRequestMessage Request(HttpMethod method, string url, string language, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Accept-Language", language);
        if (json is not null)
        {
            request.Content = Json(json);
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public async Task Handled_errors_stay_spanish_and_english_free_whatever_the_accept_language_header_says(string language)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var branch = await CreateBranchAsync(ownerClient, "Sede Centro");
        var report = new ApiMessageReport();

        async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? json)
        {
            using var request = Request(method, url, language, json);
            return await client.SendAsync(request);
        }

        await report.CheckAsync($"[{language}] validation 400 (empty login)", await Send(_client, HttpMethod.Post, LoginUrl, "{}"), HttpStatusCode.BadRequest, validationProblem: true);
        await report.CheckAsync($"[{language}] validation 400 (weak password)", await Send(_client, HttpMethod.Post, RegisterUrl,
            $$"""{"tenantName":"{{UniqueTenantName("Idioma")}}","email":"{{UniqueEmail("idioma")}}","password":"corta"}"""), HttpStatusCode.BadRequest, validationProblem: true);
        await report.CheckAsync($"[{language}] validation 400 (conversation list paging)", await Send(ownerClient, HttpMethod.Get, "/api/conversations?page=0", null), HttpStatusCode.BadRequest, validationProblem: true);
        await report.CheckAsync($"[{language}] malformed json", await Send(_client, HttpMethod.Post, LoginUrl, "{ this is not valid json "), HttpStatusCode.BadRequest, validationProblem: true);
        await report.CheckAsync($"[{language}] wrong value type", await Send(ownerClient, HttpMethod.Post, BranchesUrl, """{"name":123}"""), HttpStatusCode.BadRequest, validationProblem: true);
        await report.CheckAsync($"[{language}] 401 invalid credentials", await Send(_client, HttpMethod.Post, LoginUrl,
            $$"""{"email":"{{owner.OwnerEmail}}","password":"Wr0ng-Pass-Value-123"}"""), HttpStatusCode.Unauthorized);
        await report.CheckAsync($"[{language}] 404 unknown branch", await Send(ownerClient, HttpMethod.Get, $"{BranchesUrl}/{Guid.NewGuid()}", null), HttpStatusCode.NotFound);
        await report.CheckAsync($"[{language}] 409 duplicate email", await Send(_client, HttpMethod.Post, RegisterUrl,
            $$"""{"tenantName":"{{UniqueTenantName("Duplicada")}}","email":"{{owner.OwnerEmail}}","password":"{{StrongPassword}}"}"""), HttpStatusCode.Conflict);
        await report.CheckAsync($"[{language}] 409 duplicate branch name", await Send(ownerClient, HttpMethod.Post, BranchesUrl,
            $$"""{"name":"{{branch.Name}}"}"""), HttpStatusCode.Conflict);

        report.AssertClean();

        // The exact canonical texts, too: the language header must not select another wording.
        using var unauthorized = await Send(_client, HttpMethod.Post, LoginUrl, $$"""{"email":"{{owner.OwnerEmail}}","password":"Wr0ng-Pass-Value-123"}""");
        Assert.Equal(ApiMessages.InvalidCredentials, (await ReadProblemJsonAsync(unauthorized)).Detail);
        using var duplicate = await Send(ownerClient, HttpMethod.Post, BranchesUrl, $$"""{"name":"{{branch.Name}}"}""");
        Assert.Equal(ApiMessages.DuplicateBranchName, (await ReadProblemJsonAsync(duplicate)).Detail);
        using var validation = await Send(_client, HttpMethod.Post, LoginUrl, "{}");
        Assert.Equal(ApiMessages.ValidationTitle, (await ReadProblemJsonAsync(validation)).Title);
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public async Task The_rate_limit_answer_stays_spanish_whatever_the_accept_language_header_says(string language)
    {
        var limited = _factory.WithLoginRateLimit(1, 3600);
        using var client = RateLimitTestSupport.NewClient(limited);
        using (var first = Request(HttpMethod.Post, LoginUrl, language, "{not json"))
        using (await client.SendAsync(first)) // spends the single permit
        {
        }

        using var second = Request(HttpMethod.Post, LoginUrl, language, "{not json");
        using var throttled = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(ApiMessages.RateLimited, (await ReadProblemJsonAsync(throttled)).Detail);
        var report = new ApiMessageReport();
        await report.CheckAsync($"[{language}] 429", throttled, HttpStatusCode.TooManyRequests);
        report.AssertClean();
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public async Task The_unexpected_failure_answer_stays_spanish_whatever_the_accept_language_header_says(string language)
    {
        const string secret = "sql-connection-password-in-the-exception";
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IAuthService)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddScoped<IAuthService>(_ => new ThrowingAuthService(secret));
        }));
        using var client = host.CreateClient();
        using var request = Request(HttpMethod.Post, LoginUrl, language, $$"""{"email":"{{UniqueEmail("boom")}}","password":"{{StrongPassword}}"}""");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body);
        TestHelpers.AssertNoInternalsLeaked(body);
        Assert.Equal(ApiMessages.Unexpected, (await ReadProblemJsonAsync(response)).Detail);
        Assert.Matches(ErrorContract.CodePattern, Assert.Single(response.Headers.GetValues(ErrorContract.HeaderName)));
        var report = new ApiMessageReport();
        await report.CheckAsync($"[{language}] 500", response, HttpStatusCode.InternalServerError);
        report.AssertClean();
    }

    [Fact]
    public void The_helper_knows_the_framework_defaults_the_requirement_names()
    {
        foreach (var english in new[]
        {
            "One or more validation errors occurred.",
            "The Email field is not a valid e-mail address.",
            "A non-empty request body is required.",
            "The JSON value could not be converted to System.Int32. Path: $.price | LineNumber: 0 | BytePositionInLine: 15.",
        })
        {
            Assert.True(EnglishLeaks.IsFrameworkDefault(english), english);
            Assert.NotEmpty(EnglishLeaks.Find(english));
        }

        Assert.Empty(EnglishLeaks.Find(ApiMessages.ValidationTitle));
        Assert.Empty(EnglishLeaks.Find(ApiMessages.DuplicateBranchName));
    }

    /// <summary>Stands in for the login use case and fails the way an infrastructure fault would, with a message that must never reach the caller.</summary>
    private sealed class ThrowingAuthService(string secret) : IAuthService
    {
        public Task<Conversia.Application.DTOs.Auth.AuthResponse> RegisterTenantAsync(Conversia.Application.DTOs.Auth.RegisterTenantRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(secret);

        public Task<Conversia.Application.DTOs.Auth.AuthResponse> LoginAsync(Conversia.Application.DTOs.Auth.LoginRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(secret);
    }
}
