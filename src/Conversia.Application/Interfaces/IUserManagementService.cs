using Conversia.Application.DTOs.Users;

namespace Conversia.Application.Interfaces;

public interface IUserManagementService
{
    /// <summary>
    /// Creates a non-owner user without permissions inside
    /// <paramref name="callerTenantId"/>. Throws
    /// <see cref="Exceptions.DuplicateEmailException"/> when the email is
    /// already registered in any tenant. Backs <c>POST /api/users</c>.
    /// </summary>
    Task<UserSummaryResponse> CreateUserAsync(Guid callerTenantId, CreateUserRequest request, CancellationToken cancellationToken = default);
}
