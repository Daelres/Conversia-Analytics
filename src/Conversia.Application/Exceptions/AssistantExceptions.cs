namespace Conversia.Application.Exceptions;

/// <summary>A refusal of the AI sales assistant's use cases (docs/ASSISTANT.md). The API answers <see cref="StatusCode"/> with <see cref="Code"/>.</summary>
public class AssistantException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static AssistantException KnowledgeVersionNotFound()
        => new(404, "knowledge_version_not_found", "Versión de la base de conocimiento no encontrada.");

    public static AssistantException TermsOutdated(string current)
        => new(409, "assistant_terms_outdated", $"Los términos del asistente cambiaron: acepta la versión vigente ({current}) para activarlo.");

    public static AssistantException NotOwner()
        => new(403, "assistant_owner_only", "Solo la persona propietaria de la empresa puede activar el asistente.");

    public static AssistantException NotAvailable()
        => new(409, "assistant_not_available", "El asistente no está activo en esta conversación.");
}
