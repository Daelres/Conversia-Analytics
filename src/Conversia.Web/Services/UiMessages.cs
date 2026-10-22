using Conversia.Domain.Common;

namespace Conversia.Web.Services;

/// <summary>
/// The Spanish wording that more than one page shows. Text that belongs to a single page lives in that page's markup;
/// the validation messages of the shared DTOs live in <c>ValidationMessages</c> (Application) .
/// </summary>
public static class UiMessages
{
    public const string Unavailable = "El servicio no está disponible en este momento. Inténtalo de nuevo más tarde.";
    public const string NotAuthorized = "No tienes permiso para hacer eso.";
    public const string InvalidCredentials = "Correo o contraseña incorrectos.";
    public const string EmailTaken = "Ese correo ya está registrado.";
    public const string UserNotFound = "Usuario no encontrado.";
    public const string InvalidRequest = "La solicitud no es válida.";
    public const string NotFound = "No encontramos lo que buscas.";
    public const string AntiforgeryRejected = "El formulario no es válido o ha caducado. Recarga la página e inténtalo de nuevo.";

    public const string QuotaFallback = "No se pudo activar la línea: tu cupo de líneas activas está completo.";
    public const string ContactConversia = "Para activar otra, desactiva una de las que tienes o pide al equipo de Conversia ampliar tu cupo.";

    public static string TooManyAttempts(int? retryAfterSeconds)
        => retryAfterSeconds is { } seconds
            ? $"Demasiados intentos. Vuelve a intentarlo en {Seconds(seconds)}."
            : "Demasiados intentos. Vuelve a intentarlo más tarde.";

    /// <summary>"1 segundo", "37 segundos": the number is written plainly, so it can be read back as typed.</summary>
    public static string Seconds(int seconds)
        => seconds == 1 ? "1 segundo" : $"{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} segundos";

    /// <summary>The message for the outcomes every form treats alike; null for the ones a form words itself.</summary>
    public static string? ForCommonFailure<T>(ApiResult<T> result) => result.Status switch
    {
        ApiResultStatus.Unavailable => Unavailable,
        ApiResultStatus.Forbidden => NotAuthorized,
        ApiResultStatus.NotFound => UserNotFound,
        ApiResultStatus.RateLimited => TooManyAttempts(result.RetryAfterSeconds),
        _ => null
    };
}
