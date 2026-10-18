using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class UserManagementService : IUserManagementService
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasherService _passwordHasherService;

    public UserManagementService(IUserRepository userRepository, IPasswordHasherService passwordHasherService)
    {
        _userRepository = userRepository;
        _passwordHasherService = passwordHasherService;
    }

    public async Task<UserSummaryResponse> CreateUserAsync(Guid callerTenantId, CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        // Cheap early exit that skips hashing; the unique index (surfaced by
        // AddAsync as DuplicateEmailException) remains the race arbiter.
        if (await _userRepository.ExistsByEmailAsync(request.Email, cancellationToken))
        {
            throw new DuplicateEmailException();
        }

        var passwordHash = _passwordHasherService.HashPassword(request.Password);
        var user = User.Create(callerTenantId, request.Email, passwordHash, isOwner: false);

        await _userRepository.AddAsync(user, cancellationToken);

        return UserSummaryResponse.From(user);
    }
}
