using Conversia.Application.Interfaces;

namespace Conversia.Application.Services;

/// <summary>
/// A password hash to verify against when the administrator's email is unknown or the account is deactivated, so every failure costs one
/// verification and the response time does not tell the cases from a wrong password. Built once at startup (not on the first unknown email,
/// which would be the one slower request that gives the first guess away) and shared.
/// </summary>
public sealed class AdminLoginDecoy
{
    public AdminLoginDecoy(string hash)
    {
        Hash = hash;
    }

    public string Hash { get; }

    public static AdminLoginDecoy Create(IPasswordHasherService hasher) => new(hasher.HashPassword(Guid.NewGuid().ToString("N")));
}
