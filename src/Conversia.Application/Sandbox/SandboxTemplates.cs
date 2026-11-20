using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.Sandbox;

/// <summary>
/// The templates of a channel while its company is in the internal sandbox mode (docs/SANDBOX.md): a small fixed list, the same for every
/// channel, never stored and never synced. The internal sender accepts any of them and nothing else, so a person can try the template
/// picker without a 360dialog key and without approved templates.
/// </summary>
public static class SandboxTemplates
{
    private static readonly (string Name, TemplateCategory Category, string Body)[] Seeds =
    [
        ("conversia_retomar_conversacion", TemplateCategory.Utility, "Hola {{1}}, te escribimos para continuar con tu consulta. ¿Sigues interesado?"),
        ("conversia_recordatorio_cita", TemplateCategory.Utility, "Hola {{1}}, te recordamos tu cita del {{2}}. Responde a este mensaje si necesitas cambiarla."),
        ("conversia_promocion", TemplateCategory.Marketing, "{{1}}, esta semana tenemos una promoción especial para ti. ¿Quieres conocerla?"),
    ];

    /// <summary>Spanish, like the pilot's customers.</summary>
    public const string Language = "es";

    /// <summary>The templates of <paramref name="channel"/> in sandbox mode, approved, as of <paramref name="now"/>. New objects each time: never tracked.</summary>
    public static IReadOnlyList<MessageTemplate> For(WhatsAppChannel channel, DateTime now)
        => [.. Seeds.Select(s => MessageTemplate.Create(channel.TenantId, channel.Id, s.Name, Language, s.Category, TemplateStatus.Approved, s.Body, true, now))];

    public static MessageTemplate? Find(WhatsAppChannel channel, string name, string language, DateTime now)
        => For(channel, now).FirstOrDefault(t => t.Name == name && t.Language == language);
}
