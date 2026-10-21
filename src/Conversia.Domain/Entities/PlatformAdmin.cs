using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// A person who runs the platform: approves the client companies and sets their line quota. Global: it belongs to no company, has no
/// <c>TenantId</c> and is not reachable through the tenant query filters. Never has access to a company's conversations or users.
/// </summary>
public class PlatformAdmin : BaseEntity
{
    public const int EmailMaxLength = 256;

    private static readonly Regex EmailPattern = new(@"^[^@\s\0]+@[^@\s\0]+\.[^@\s\0]+$", RegexOptions.Compiled);

    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    // Required by EF Core for materialization.
    private PlatformAdmin()
    {
    }

    public static PlatformAdmin Create(string email, string passwordHash)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > EmailMaxLength || !EmailPattern.IsMatch(trimmed) || TextRules.HasUnpairedSurrogate(trimmed))
        {
            throw new DomainException("El correo electrónico del administrador no es válido.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("El hash de la contraseña es obligatorio.");
        }

        return new PlatformAdmin { Email = trimmed, PasswordHash = passwordHash };
    }

    public void Deactivate() => IsActive = false;
}
