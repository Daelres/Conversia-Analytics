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

/// <summary>
/// Too many failed sign-ins on a platform administrator account: the attempt is refused before the password is looked at. The API layer
/// maps this to 429 with <c>Retry-After</c> and the same text whatever the email, so it never tells which emails exist.
/// </summary>
public class LoginThrottledException : Exception
{
    public const string DefaultMessage = "Demasiados intentos de inicio de sesión. Inténtalo de nuevo más tarde.";

    public LoginThrottledException(TimeSpan retryAfter)
        : base(DefaultMessage)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}
