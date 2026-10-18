using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Conversia.Infrastructure.Services;

public class PasswordHasherService : IPasswordHasherService
{
    private readonly PasswordHasher<User> _passwordHasher = new();

    public string HashPassword(string password)
        => _passwordHasher.HashPassword(null!, password);

    public bool VerifyPassword(string passwordHash, string providedPassword)
        => _passwordHasher.VerifyHashedPassword(null!, passwordHash, providedPassword) != PasswordVerificationResult.Failed;
}
