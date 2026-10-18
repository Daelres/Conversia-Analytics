using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Login must not apply the registration password policy: users whose hash
/// predates the policy (seeded directly, bypassing registration) can still
/// log in, wrong passwords stay a generic 401, and a missing password stays 400.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class LoginLegacyPasswordTests
{
    private const string LoginUrl = "/api/auth/login";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LoginLegacyPasswordTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Creates a user with a weak password straight through the repository,
    /// the way a pre-policy account would exist. Needs a real tenant because
    /// of the Users -> Tenants foreign key, so one is registered first.
    /// </summary>
    private async Task<string> SeedLegacyUserAsync(string weakPassword)
    {
        var (_, tenantId) = await RegisterOwnerAsync(_client, "Legacy Co");

        var email = UniqueEmail("legacy");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        await users.AddAsync(User.Create(tenantId, email, hasher.HashPassword(weakPassword), isOwner: false));
        return email;
    }

    [Theory]
    [InlineData("a")]
    [InlineData("password")]
    [InlineData("ALLUPPERCASE")]
    public async Task Login_WithLegacyWeakPassword_StillSucceeds(string weakPassword)
    {
        var email = await SeedLegacyUserAsync(weakPassword);

        var response = await _client.PostAsJsonAsync(LoginUrl, new { email, password = weakPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Theory]
    [InlineData("b")]
    [InlineData("wrong")]
    public async Task Login_WithWrongWeakPassword_Returns401NotAValidationError(string wrongPassword)
    {
        var email = await SeedLegacyUserAsync("password");

        var response = await _client.PostAsJsonAsync(LoginUrl, new { email, password = wrongPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithVeryLongPassword_ReturnsControlledClientError()
    {
        var email = await SeedLegacyUserAsync("password");

        var response = await _client.PostAsJsonAsync(LoginUrl, new { email, password = new string('x', 10_000) });

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest,
            $"Expected 401 or 400 but got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Login_WithEmptyPassword_Returns400()
    {
        var email = await SeedLegacyUserAsync("password");

        var response = await _client.PostAsJsonAsync(LoginUrl, new { email, password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutPasswordProperty_Returns400()
    {
        var email = await SeedLegacyUserAsync("password");

        var response = await _client.PostAsJsonAsync(LoginUrl, new { email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
