using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class User : BaseEntity
{
    private static readonly Regex EmailPattern = new(@"^[^@\s\0]+@[^@\s\0]+\.[^@\s\0]+$", RegexOptions.Compiled);

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsOwner { get; private set; }

    // Required by EF Core for materialization.
    private User()
    {
    }

    public static User Create(Guid tenantId, string email, string passwordHash, bool isOwner)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("El identificador del espacio de trabajo es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DomainException("El correo electrónico es obligatorio.");
        }

        if (!EmailPattern.IsMatch(email))
        {
            throw new DomainException("El formato del correo electrónico no es válido.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("El hash de la contraseña es obligatorio.");
        }

        return new User
        {
            TenantId = tenantId,
            Email = email,
            PasswordHash = passwordHash,
            IsOwner = isOwner
        };
    }
}
