using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class PlatformAdminAuthService : IPlatformAdminAuthService
{
    // A hash to verify against when the email is unknown or the administrator is deactivated, so every failure costs one password
    // verification and the response time does not tell the two cases from a wrong password.
    private static string? _decoy;

    private readonly IPlatformAdminRepository _admins;
    private readonly IPasswordHasherService _hasher;
    private readonly IPlatformAdminTokenService _tokens;

    public PlatformAdminAuthService(IPlatformAdminRepository admins, IPasswordHasherService hasher, IPlatformAdminTokenService tokens)
    {
        _admins = admins;
        _hasher = hasher;
        _tokens = tokens;
    }

    public async Task<AdminAuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var admin = await _admins.GetByEmailAsync(request.Email.Trim(), cancellationToken);

        var hash = admin is { IsActive: true } ? admin.PasswordHash : _decoy ??= _hasher.HashPassword(Guid.NewGuid().ToString("N"));
        var verified = _hasher.VerifyPassword(hash, request.Password);
        if (admin is not { IsActive: true } || !verified)
        {
            throw new InvalidCredentialsException();
        }

        var (token, expiresAt) = _tokens.Issue(admin.Id);
        return new AdminAuthResponse { Token = token, AdminId = admin.Id, ExpiresAt = expiresAt };
    }
}
