namespace Conversia.Application.DTOs.Auth;

/// <summary>
/// Response returned by both register-tenant and login. Carries the issued
/// JWT plus the claims already encoded in it, for convenience.
/// </summary>
public class AuthResponse
{
    public string Token { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public bool IsOwner { get; set; }
}
