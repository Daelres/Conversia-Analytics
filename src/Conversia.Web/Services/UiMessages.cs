using Conversia.Domain.Common;

namespace Conversia.Web.Services;

/// <summary>
/// The Spanish wording that more than one page shows. Text that belongs to a single page lives in that page's markup;
/// the validation messages of the shared DTOs live in <c>ValidationMessages</c> (Application) and the demo account texts in <c>DemoAccounts</c>.
/// </summary>
public static class UiMessages
{
    public const string Unavailable = "El servicio no está disponible en este momento. Inténtalo de nuevo más tarde.";
    public const string NotAuthorized = "No tienes permiso para hacer eso.";
    public const string InvalidCredentials = "Correo o contraseña incorrectos.";
    public const string EmailTaken = "Ese correo ya está registrado.";
    public const string UserNotFound = "Usuario no encontrado.";
    public const string InvalidRequest = "La solicitud no es válida.";
    public const string ProductNotFound = "Producto no encontrado.";
    public const string ProductSkuTaken = "Ya existe un producto con ese SKU.";
    public const string ProductModified = "Otra persona modificó este producto. Recarga la página para ver la última versión.";
    public const string PriceNotANumber = "El precio debe ser un número igual o mayor que 0, con hasta dos decimales, escrito con coma o con punto (por ejemplo, 12,50).";
    public const string StockInvalid = "El stock debe ser un número entero igual o mayor que 0.";
    public const string AntiforgeryRejected = "El formulario no es válido o ha caducado. Recarga la página e inténtalo de nuevo.";

    public static string TooManyAttempts(int? retryAfterSeconds)
        => retryAfterSeconds is { } seconds
            ? $"Demasiados intentos. Vuelve a intentarlo en {Seconds(seconds)}."
            : "Demasiados intentos. Vuelve a intentarlo más tarde.";

    /// <summary>"1 segundo", "37 segundos": the number is written plainly, so it can be read back as typed.</summary>
    public static string Seconds(int seconds)
        => seconds == 1 ? "1 segundo" : $"{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} segundos";

    /// <summary>"1 producto", "1.234 productos".</summary>
    public static string ProductCount(int count)
        => count == 1 ? "1 producto" : $"{SpanishCulture.FormatWhole(count)} productos";

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
