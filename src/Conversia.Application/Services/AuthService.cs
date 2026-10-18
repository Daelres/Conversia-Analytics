using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class AuthService : IAuthService
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasherService _passwordHasherService;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthService(
        ITenantRepository tenantRepository,
        IUserRepository userRepository,
        IPasswordHasherService passwordHasherService,
        IJwtTokenService jwtTokenService)
    {
        _tenantRepository = tenantRepository;
        _userRepository = userRepository;
        _passwordHasherService = passwordHasherService;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<AuthResponse> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken cancellationToken = default)
    {
        if (await _userRepository.ExistsByEmailAsync(request.Email, cancellationToken))
        {
            throw new DuplicateEmailException();
        }

        var tenant = Tenant.Create(request.TenantName);
        var hashedPassword = _passwordHasherService.HashPassword(request.Password);
        var user = User.Create(tenant.Id, request.Email, hashedPassword, isOwner: true);

        await _tenantRepository.AddAsync(tenant, cancellationToken);
        await _userRepository.AddAsync(user, cancellationToken);

        var token = _jwtTokenService.GenerateToken(user.Id, tenant.Id, isOwner: true);

        return new AuthResponse
        {
            Token = token,
            UserId = user.Id,
            TenantId = tenant.Id,
            IsOwner = true
        };
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByEmailAsync(request.Email, cancellationToken);

        if (user is null || !_passwordHasherService.VerifyPassword(user.PasswordHash, request.Password))
        {
            throw new InvalidCredentialsException();
        }

        var token = _jwtTokenService.GenerateToken(user.Id, user.TenantId, user.IsOwner);

        return new AuthResponse
        {
            Token = token,
            UserId = user.Id,
            TenantId = user.TenantId,
            IsOwner = user.IsOwner
        };
    }
}
