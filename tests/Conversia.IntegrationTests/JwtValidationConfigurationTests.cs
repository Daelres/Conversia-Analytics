using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// End-to-end proof that the API validates bearer tokens with the configured
/// signing key — tokens are minted here independently of the API's own token service.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class JwtValidationConfigurationTests
{
    private const string OtherSigningKey = "a-completely-different-signing-key-of-sufficient-length-0123";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public JwtValidationConfigurationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string MintToken(
        Guid userId,
        Guid tenantId,
        string signingKey = TestJwtSettings.SigningKey)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("is_owner", "true")
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(TestJwtSettings.DefaultIssuer, TestJwtSettings.DefaultAudience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpStatusCode> GetMeAsync(string token)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.GetAsync("/api/users/me")).StatusCode;
    }

    [Fact]
    public async Task Token_SignedWithTheConfiguredKeyAndDefaultIssuerAndAudience_IsAccepted()
    {
        var (userId, tenantId) = await RegisterOwnerAsync(_client, "Jwt Co");

        var status = await GetMeAsync(MintToken(userId, tenantId));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Token_SignedWithADifferentKey_IsRejectedWith401()
    {
        var (userId, tenantId) = await RegisterOwnerAsync(_client, "Jwt Co");

        var status = await GetMeAsync(MintToken(userId, tenantId, signingKey: OtherSigningKey));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Token_SignedWithTheShippedPlaceholderKey_IsRejectedWith401()
    {
        var (userId, tenantId) = await RegisterOwnerAsync(_client, "Jwt Co");
        var placeholder = "REPLACE_WITH_A_STRONG_SECRET_AT_LEAST_32_BYTES_LONG";

        var status = await GetMeAsync(MintToken(userId, tenantId, signingKey: placeholder));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
