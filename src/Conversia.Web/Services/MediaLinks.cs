using System.Globalization;

namespace Conversia.Web.Services;

/// <summary>The Web's addresses and Spanish words for the media of a conversation (docs/MESSAGING.md, "Media").</summary>
public static class MediaLinks
{
    /// <summary>The Web's pass-through of a message's stored file.</summary>
    public static string File(Guid conversationId, Guid messageId) => $"/conversaciones/{conversationId}/adjuntos/{messageId}";

    public static bool IsFilePath(string? path)
        => path is not null && path.StartsWith("/conversaciones/", StringComparison.OrdinalIgnoreCase) && path.Contains("/adjuntos/", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the browser offers to attach: Meta's image and document types (the API checks the content again).</summary>
    public const string Accept =
        "image/jpeg,image/png,application/pdf,text/plain,application/msword,application/vnd.ms-excel,application/vnd.ms-powerpoint,"
        + "application/vnd.openxmlformats-officedocument.wordprocessingml.document,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,"
        + "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static string DocumentLabel(string? contentType) => contentType switch
    {
        "application/pdf" => "(PDF)",
        "text/plain" => "(texto)",
        "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "(Word)",
        "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "(Excel)",
        "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "(PowerPoint)",
        _ => string.Empty,
    };

    public static string Size(long? bytes) => bytes switch
    {
        null => string.Empty,
        < 1024 => $" · {bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.GetCultureInfo("es-CO"), $" · {bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.GetCultureInfo("es-CO"), $" · {bytes / (1024.0 * 1024):0.#} MB"),
    };

    public static string StatusText(string status, string kind) => status switch
    {
        "pending" => "Descargando el archivo: aparecerá aquí en unos minutos.",
        "too_large" => "El archivo es demasiado grande para guardarlo.",
        "blocked" => "Este tipo de archivo no está permitido y no se guardó.",
        "expired" => "El archivo ya no está disponible en WhatsApp: no se alcanzó a descargar.",
        "failed" => "No se pudo descargar el archivo.",
        "purged" => "El archivo se eliminó al cumplirse el tiempo de retención.",
        _ => "Archivo no disponible.",
    };

    /// <summary>The notice of the attachment composer after a send (the query string of the redirect).</summary>
    public static string? Notice(string? code) => code switch
    {
        null or "" => null,
        "enviado" => "El archivo se envió al cliente.",
        "simulado" => "El adjunto de prueba entró como un mensaje del cliente.",
        "vacio" => "Elige un archivo para enviar.",
        _ => SendFailure.Describe(code) ?? SendFailure.OutcomeUnknown,
    };
}
