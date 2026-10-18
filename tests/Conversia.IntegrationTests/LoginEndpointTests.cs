using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Conversia.Application.DTOs.Auth;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

[Collection(nameof(IntegrationTestCollection))]
public class LoginEndpointTests
{
    private readonly HttpClient _client;

    public LoginEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<(string Email, string Password)> RegisterUserAsync()
    {
        var email = UniqueEmail("login");
        const string password = "Sup3r-Secret!";
        var payload = new { tenantName = $"Login Co {Guid.NewGuid():N}", email, password };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);

        // Login tests depend on registration succeeding first. At RED state
        // (AuthService.RegisterTenantAsync throws NotImplementedException)
        // this assertion fails, which correctly fails every test in this
        // class until Role C implements RegisterTenantAsync too.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (email, password);
    }

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithJwtContainingUserTenantAndOwnerClaims()
    {
        var (email, password) = await RegisterUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(body.Token);

        Assert.Contains(jwt.Claims, c => c.Type is "sub" or JwtRegisteredClaimNames.Sub);
        Assert.Contains(jwt.Claims, c => c.Type == "tenant_id");
        Assert.Contains(jwt.Claims, c => c.Type == "is_owner");
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (email, _) = await RegisterUserAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = "totally-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401WithBodyByteForByteIdenticalToWrongPasswordCase()
    {
        // Security assertion from docs/USERS_TENANTS_PERMISSIONS.md: wrong
        // password and unknown email must be indistinguishable to the caller.
        var (knownEmail, _) = await RegisterUserAsync();
        var unknownEmail = UniqueEmail("nobody");

        var wrongPasswordResponse = await _client.PostAsJsonAsync(
            "/api/auth/login", new { email = knownEmail, password = "totally-wrong" });
        var unknownEmailResponse = await _client.PostAsJsonAsync(
            "/api/auth/login", new { email = unknownEmail, password = "whatever" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmailResponse.StatusCode);

        var wrongPasswordBytes = await wrongPasswordResponse.Content.ReadAsByteArrayAsync();
        var unknownEmailBytes = await unknownEmailResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(wrongPasswordBytes, unknownEmailBytes);
    }

    [Fact]
    public async Task Login_WithMalformedJsonBody_Returns400NeverAnUnhandledException()
    {
        var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongContentType_Returns400Or415NeverAnUnhandledException()
    {
        var content = new StringContent("email=a@a.test&password=x", Encoding.UTF8, "text/plain");

        var response = await _client.PostAsync("/api/auth/login", content);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Expected 400 or 415, got {(int)response.StatusCode}.");
    }
}
