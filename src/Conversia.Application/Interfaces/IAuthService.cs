using Conversia.Application.DTOs.Auth;

namespace Conversia.Application.Interfaces;

public interface IAuthService
{
    /// <summary>
    /// Creates a new tenant and its first (owner) user in one transaction,
    /// then returns an issued JWT for that user.
    /// Throws <see cref="Exceptions.DuplicateEmailException"/> if the email
    /// is already registered.
    /// </summary>
    Task<AuthResponse> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates credentials and returns an issued JWT on success.
    /// Throws <see cref="Exceptions.InvalidCredentialsException"/> for both
    /// unknown email and wrong password — indistinguishable to the caller.
    /// </summary>
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}
