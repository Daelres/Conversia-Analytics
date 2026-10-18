namespace Conversia.Application.Exceptions;

/// <summary>
/// Thrown when tenant registration is attempted with an email that is
/// already registered to another user. The API layer maps this to
/// 409 Conflict with a generic message.
/// </summary>
public class DuplicateEmailException : Exception
{
    public const string DefaultMessage = "Ese correo ya está registrado.";

    public DuplicateEmailException()
        : base(DefaultMessage)
    {
    }

    public DuplicateEmailException(string message) : base(message)
    {
    }
}
