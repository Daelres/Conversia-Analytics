namespace Conversia.Application.Messages;

/// <summary>The Spanish name of a WhatsApp message type, as the messages feed and the conversation pages write it.</summary>
public static class MessageTypeLabels
{
    public static string For(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "text" => "Texto",
        "image" => "Imagen",
        "audio" => "Audio",
        "video" => "Video",
        "document" => "Documento",
        "sticker" => "Sticker",
        "location" => "Ubicación",
        "contacts" => "Contacto",
        "reaction" => "Reacción",
        "interactive" => "Mensaje interactivo",
        "button" => "Botón",
        _ => string.IsNullOrWhiteSpace(type) ? "Mensaje" : "Mensaje (" + type + ")",
    };
}
