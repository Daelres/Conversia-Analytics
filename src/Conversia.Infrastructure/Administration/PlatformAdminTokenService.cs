using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Conversia.Infrastructure.Administration;

public sealed class PlatformAdminTokenService : IPlatformAdminTokenService
{
    private readonly JwtOptions _jwt;
    private readonly PlatformAdminOptions _options;
    private readonly TimeProvider _time;

    public PlatformAdminTokenService(IOptions<JwtOptions> jwt, IOptions<PlatformAdminOptions> options, TimeProvider time)
    {
        _jwt = jwt.Value;
        _options = options.Value;
        _time = time;
    }

    public (string Token, DateTime ExpiresAt) Issue(Guid adminId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.TokenMinutes);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: PlatformAdminTokens.AudienceOf(_jwt),
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, adminId.ToString()),
                new Claim(PlatformAdminTokens.RoleClaimType, PlatformAdminTokens.RoleValue),
            ],
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(PlatformAdminTokens.KeyOf(_jwt), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
