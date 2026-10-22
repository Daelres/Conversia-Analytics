using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class PlatformAdminAuthService : IPlatformAdminAuthService
{
    private readonly IPlatformAdminRepository _admins;
    private readonly IPasswordHasherService _hasher;
    private readonly IPlatformAdminTokenService _tokens;
    private readonly AdminLoginDecoy _decoy;
    private readonly IAdminLoginThrottle _throttle;

    public PlatformAdminAuthService(
        IPlatformAdminRepository admins, IPasswordHasherService hasher, IPlatformAdminTokenService tokens, AdminLoginDecoy decoy, IAdminLoginThrottle throttle)
    {
        _admins = admins;
        _hasher = hasher;
        _tokens = tokens;
        _decoy = decoy;
        _throttle = throttle;
    }

    public async Task<AdminAuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();

        // An account that failed too often is refused before anything else is looked at, known or not: the answer is the same for every email.
        if (_throttle.RetryAfter(email) is { } wait)
        {
            throw new LoginThrottledException(wait);
        }

        var admin = await _admins.GetByEmailAsync(email, cancellationToken);

        var hash = admin is { IsActive: true } ? admin.PasswordHash : _decoy.Hash;
        var verified = _hasher.VerifyPassword(hash, request.Password);
        if (admin is not { IsActive: true } || !verified)
        {
            _throttle.RecordFailure(email);
            throw new InvalidCredentialsException();
        }

        _throttle.Reset(email);
        var (token, expiresAt) = _tokens.Issue(admin.Id);
        return new AdminAuthResponse { Token = token, AdminId = admin.Id, ExpiresAt = expiresAt };
    }
}
