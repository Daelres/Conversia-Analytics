namespace Conversia.Application.Exceptions;

/// <summary>
/// Thrown on login when the email is unknown OR the password is wrong.
/// Deliberately the SAME exception type/message for both cases — the caller
/// must never be able to distinguish "unknown email" from "wrong password".
/// The API layer maps this to 401 Unauthorized with a generic message.
/// </summary>
public class InvalidCredentialsException : Exception
{
    public const string DefaultMessage = "Correo o contraseña incorrectos.";

    public InvalidCredentialsException()
        : base(DefaultMessage)
    {
    }
}
