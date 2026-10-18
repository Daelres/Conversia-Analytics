namespace Conversia.Application.Exceptions;

/// <summary>
/// Thrown when a requested user does not exist, or exists but belongs to a
/// different tenant than the caller. The API layer maps this to 404 so a
/// cross-tenant probe cannot distinguish "wrong tenant" from "does not exist"
/// — see docs/USERS_TENANTS_PERMISSIONS.md, tenant isolation boundary.
/// </summary>
public class UserNotFoundException : Exception
{
    public const string DefaultMessage = "Usuario no encontrado.";

    public UserNotFoundException()
        : base(DefaultMessage)
    {
    }

    public UserNotFoundException(string message) : base(message)
    {
    }
}
