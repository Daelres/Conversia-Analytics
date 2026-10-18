using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Password policy on <c>POST /api/auth/register-tenant</c>: 12–128
/// characters with at least one uppercase letter, one lowercase letter and
/// one digit. Violations are a 400 <see cref="ValidationProblemDetails"/>
/// with an error under the <c>Password</c> key, and nothing is persisted.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class RegisterTenantPasswordPolicyTests
{
    private const string RegisterUrl = "/api/auth/register-tenant";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegisterTenantPasswordPolicyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> RegisterAsync(string email, string? password, string? tenantName = null) =>
        _client.PostAsJsonAsync(RegisterUrl, new { tenantName = tenantName ?? UniqueTenantName("Policy Co"), email, password });

    private async Task<bool> UserExistsAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email);
    }

    private async Task<bool> TenantExistsAsync(string tenantName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Name == tenantName);
    }

    private static async Task AssertPasswordValidationFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(
            problem!.Errors.TryGetValue("Password", out var messages) && messages.Length > 0,
            $"Expected a validation error under 'Password' but got keys: {string.Join(", ", problem.Errors.Keys)}.");
    }

    public static TheoryData<string> WeakPasswords => new()
    {
        "Abcdefghi1k",                       // 11 characters, one below the minimum
        "abcdefghijk1",                      // no uppercase
        "ABCDEFGHIJK1",                      // no lowercase
        "Abcdefghijkl",                      // no digit
        "Aa1",                               // far too short
        "            ",                      // whitespace only
        "Aa1" + new string('x', 126),        // 129 characters, one above the maximum
        "Aa1" + new string('x', 9_997),      // 10 000 characters
    };

    [Theory]
    [MemberData(nameof(WeakPasswords))]
    public async Task RegisterTenant_WithPasswordViolatingThePolicy_Returns400ValidationErrorAndWritesNothing(string password)
    {
        var email = UniqueEmail("weak");
        var tenantName = UniqueTenantName("Policy Co");

        var response = await RegisterAsync(email, password, tenantName);

        await AssertPasswordValidationFailureAsync(response);
        Assert.False(await UserExistsAsync(email));
        Assert.False(await TenantExistsAsync(tenantName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RegisterTenant_WithNullOrEmptyPassword_Returns400ValidationErrorAndWritesNothing(string? password)
    {
        var email = UniqueEmail("blank");
        var tenantName = UniqueTenantName("Policy Co");

        var response = await RegisterAsync(email, password, tenantName);

        await AssertPasswordValidationFailureAsync(response);
        Assert.False(await UserExistsAsync(email));
        Assert.False(await TenantExistsAsync(tenantName));
    }

    [Fact]
    public async Task RegisterTenant_WithoutPasswordProperty_Returns400ValidationErrorAndWritesNothing()
    {
        var email = UniqueEmail("absent");
        var tenantName = UniqueTenantName("Policy Co");

        var response = await _client.PostAsJsonAsync(RegisterUrl, new { tenantName, email });

        await AssertPasswordValidationFailureAsync(response);
        Assert.False(await UserExistsAsync(email));
        Assert.False(await TenantExistsAsync(tenantName));
    }

    [Fact]
    public async Task RegisterTenant_WithWeakPassword_DoesNotEchoThePasswordInTheResponse()
    {
        const string weakPassword = "hunter2-weak";

        var response = await RegisterAsync(UniqueEmail("echo"), weakPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(weakPassword, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegisterTenant_WithWeakPasswordAndInvalidEmail_ReportsBothFieldErrors()
    {
        var response = await RegisterAsync("not-an-email", "weak");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("Password", problem!.Errors.Keys);
        Assert.Contains("Email", problem.Errors.Keys);
    }

    public static TheoryData<string> CompliantPasswords => new()
    {
        "Abcdefghij1k",                      // exactly 12 characters
        "Aa1" + new string('x', 125),        // exactly 128 characters
        "Ñandú-secreto-9",                   // non-ASCII uppercase letter
        "correct HORSE battery 9 staple",    // spaces allowed
    };

    [Theory]
    [MemberData(nameof(CompliantPasswords))]
    public async Task RegisterTenant_WithPasswordMeetingThePolicy_Returns201AndThePasswordWorksForLogin(string password)
    {
        var email = UniqueEmail("strong");

        var response = await RegisterAsync(email, password);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(await UserExistsAsync(email));

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task RegisterTenant_ConcurrentMixOfWeakAndStrongPasswords_EachRequestGetsItsOwnCorrectOutcome()
    {
        const int pairs = 10;
        var strongEmails = Enumerable.Range(0, pairs).Select(_ => UniqueEmail("mix-strong")).ToList();
        var weakEmails = Enumerable.Range(0, pairs).Select(_ => UniqueEmail("mix-weak")).ToList();
        var weakTenantNames = Enumerable.Range(0, pairs).Select(_ => UniqueTenantName("Policy Co")).ToList();

        var strongTasks = strongEmails.Select(e => RegisterAsync(e, StrongPassword));
        var weakTasks = weakEmails.Select((e, i) => RegisterAsync(e, "weakpassword", weakTenantNames[i]));
        var responses = await Task.WhenAll(strongTasks.Concat(weakTasks));

        Assert.All(responses.Take(pairs), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.All(responses.Skip(pairs), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        foreach (var email in strongEmails)
        {
            Assert.True(await UserExistsAsync(email));
        }

        foreach (var email in weakEmails)
        {
            Assert.False(await UserExistsAsync(email));
        }

        foreach (var tenantName in weakTenantNames)
        {
            Assert.False(await TenantExistsAsync(tenantName));
        }
    }
}
