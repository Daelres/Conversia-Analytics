using System.Text.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The append-only history of a company's assistant settings and consent (docs/ASSISTANT.md, "Trail"): every switch on (with the terms and the
/// acknowledgement), switch off, settings change (old and new values), line change and knowledge publication. Rows are never updated: the
/// previous consent stays. Ids, codes and the company's own settings only; never a customer's data.
/// </summary>
public class AssistantAuditEntry
{
    public const int ActionMaxLength = 64;
    public const int DetailsMaxLength = 8000;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid TenantId { get; private set; }

    public DateTime At { get; private set; }

    public Guid? UserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    /// <summary>A small JSON object.</summary>
    public string Details { get; private set; } = "{}";

    public static AssistantAuditEntry Record(Guid tenantId, Guid? userId, string action, IReadOnlyDictionary<string, object?> details, DateTime now)
    {
        var json = JsonSerializer.Serialize(details);
        if (string.IsNullOrWhiteSpace(action) || action.Length > ActionMaxLength || json.Length > DetailsMaxLength)
        {
            throw new DomainException("El registro del asistente no es válido.");
        }

        return new AssistantAuditEntry
        {
            TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria."),
            UserId = userId,
            Action = action,
            Details = json,
            At = UtcTime.Normalize(now),
        };
    }
}

/// <summary>The actions of <see cref="AssistantAuditEntry"/>.</summary>
public static class AssistantAuditActions
{
    public const string Enabled = "assistant.enabled";
    public const string Disabled = "assistant.disabled";
    public const string SettingsChanged = "assistant.settings_changed";
    public const string ChannelChanged = "assistant.channel_changed";
    public const string KnowledgePublished = "assistant.knowledge_published";
}
