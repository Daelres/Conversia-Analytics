namespace Conversia.IntegrationTests;

/// <summary>
/// JWT settings the test hosts run with. The signing key is injected through
/// configuration because the placeholder in the API's appsettings.json is
/// rejected at startup.
/// </summary>
internal static class TestJwtSettings
{
    // 64 ASCII characters = 64 bytes, comfortably above the 32-byte minimum.
    public const string SigningKey = "integration-tests-only-signing-key-0123456789-abcdefghijklmnopqr";

    // The API's defaults when Jwt:Issuer / Jwt:Audience are not configured.
    public const string DefaultIssuer = "Conversia";
    public const string DefaultAudience = "Conversia";
}
