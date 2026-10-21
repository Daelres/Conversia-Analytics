namespace Conversia.API.Localization;

/// <summary>The Spanish <c>title</c>/<c>detail</c> texts the API itself words (the exceptions carry their own detail).</summary>
public static class ProblemTexts
{
    public const string ValidationTitle = "Uno o más errores de validación.";

    public const string DuplicateEmailTitle = "Correo duplicado";
    public const string InvalidCredentialsTitle = "Credenciales no válidas";
    public const string UserNotFoundTitle = "Usuario no encontrado";
    public const string BranchNotFoundTitle = "Sucursal no encontrada";
    public const string AdvisorNotFoundTitle = "Asesor no encontrado";
    public const string ChannelNotFoundTitle = "Canal no encontrado";
    public const string TenantNotFoundTitle = "Empresa no encontrada";
    public const string ConversationNotFoundTitle = "Conversación no encontrada";
    public const string DuplicateBranchNameTitle = "Sucursal duplicada";
    public const string InvalidRequestTitle = "Solicitud no válida";
    public const string QueryTimeoutTitle = "Consulta demasiado lenta";

    public const string UnexpectedTitle = "Ocurrió un error inesperado";
    public const string UnexpectedDetail = "Ocurrió un error inesperado. Inténtalo de nuevo más tarde.";

    public const string TooManyRequestsTitle = "Demasiadas solicitudes";
    public const string TooManyLoginsDetail = "Demasiados intentos de inicio de sesión. Inténtalo de nuevo más tarde.";
    public const string TooManyQueriesDetail = "Demasiadas consultas en poco tiempo. Espera unos segundos e inténtalo de nuevo.";
    public const string TooManyWebhooksDetail = "Demasiadas llamadas al webhook desde esta dirección. Inténtalo de nuevo más tarde.";
    public const string WebhookUnauthorizedTitle = "Webhook no autorizado";
    public const string WebhookUnauthorizedDetail = "El canal o el secreto del webhook no son válidos.";
    public const string TooManyReportsDetail ="Demasiados reportes de errores desde esta dirección. Inténtalo de nuevo más tarde.";

    // A request the server could not read as HTTP (BadHttpRequestException): wrong framing, too slow, too big.
    public const string MalformedRequestDetail = "La solicitud está mal formada o incompleta.";
    public const string RequestTimeoutTitle = "Tiempo de espera agotado";
    public const string RequestTimeoutDetail = "Se agotó el tiempo de espera para recibir la solicitud completa.";
    public const string PayloadTooLargeTitle = "Solicitud demasiado grande";
    public const string PayloadTooLargeDetail = "El cuerpo de la solicitud supera el tamaño máximo permitido.";

    public const string PermissionNotRecognized = "El permiso indicado no es un valor reconocido.";

    // What the frameworks say when nobody translates it (model binding, JSON input, client errors without a body).
    public const string BodyRequired = "Se requiere un cuerpo de solicitud que no esté vacío.";
    public const string BodyNotJson = "El cuerpo de la solicitud no tiene el formato JSON esperado.";
    public const string ValueWrongFormat = "El valor indicado no tiene el formato esperado.";
    public const string ValueInvalid = "El valor indicado no es válido.";
    public const string ValueRequired = "Falta un valor obligatorio.";
    public const string ValueNotNumber = "El valor indicado debe ser un número.";
    public const string InputInvalid = "La entrada no es válida.";
}
