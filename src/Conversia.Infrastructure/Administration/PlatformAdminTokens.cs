using System.Security.Cryptography;
using System.Text;
using Conversia.Infrastructure.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Conversia.Infrastructure.Administration;

/// <summary>
/// What makes a platform administrator's token different from a company user's, in one place for the issuer and the validator:
/// its own audience, its own signing key (derived from the configured one, so a token of one kind can never validate as the other, even
/// with forged claims) and a <c>role</c> claim. It carries no <c>tenant_id</c>.
/// </summary>
public static class PlatformAdminTokens
{
    public const string Scheme = "PlatformAdminBearer";
    public const string PolicyName = "PlatformAdmin";
    public const string RoleClaimType = "role";
    public const string RoleValue = "platform_admin";

    private const string KeyLabel = "conversia/platform-admin/signing-key/v1";

    public static string AudienceOf(JwtOptions jwt) => jwt.Audience + ".platform-admin";

    public static SymmetricSecurityKey KeyOf(JwtOptions jwt)
        => new(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.SigningKey), Encoding.UTF8.GetBytes(KeyLabel)));
}
