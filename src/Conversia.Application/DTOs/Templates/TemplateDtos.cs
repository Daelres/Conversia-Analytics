using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Templates;

/// <summary>A template of a channel as Conversia last synced it (docs/MESSAGING.md, "Templates"). The business's own text: no customer data.</summary>
public class MessageTemplateResponse
{
    public string Name { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    /// <summary><c>Marketing</c>, <c>Utility</c>, <c>Authentication</c> or <c>Unknown</c>. Meta charges each template message by its category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary><c>Approved</c>, <c>Pending</c>, <c>Rejected</c>, <c>Paused</c>, <c>Disabled</c> or <c>Unknown</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public string? BodyText { get; set; }

    /// <summary>The placeholders, in the order their values are sent (<c>1</c>, <c>2</c> or <c>first_name</c>).</summary>
    public IReadOnlyList<string> ParameterNames { get; set; } = [];

    /// <summary>False for a template Conversia cannot send yet (media header, buttons with variables, authentication, too many fields).</summary>
    public bool IsSupported { get; set; }

    /// <summary>Approved and supported: it can be sent now.</summary>
    public bool CanBeSent { get; set; }

    public DateTime SyncedAt { get; set; }

    public static MessageTemplateResponse From(MessageTemplate template) => new()
    {
        Name = template.Name,
        Language = template.Language,
        Category = template.Category.ToString(),
        Status = template.Status.ToString(),
        BodyText = template.BodyText,
        ParameterNames = template.ParameterNames,
        IsSupported = template.IsSupported,
        CanBeSent = template.CanBeSent,
        SyncedAt = template.SyncedAt,
    };
}

/// <summary>The templates of a channel, and whether they are the internal sandbox's fixed list (docs/SANDBOX.md) instead of 360dialog's.</summary>
public class ChannelTemplatesResponse
{
    public IReadOnlyList<MessageTemplateResponse> Templates { get; set; } = [];

    /// <summary>The company is in sandbox mode: these are the fixed sandbox templates, nothing was read from 360dialog.</summary>
    public bool Sandbox { get; set; }

    /// <summary>When the channel's templates were last synced; null when never (or in sandbox mode).</summary>
    public DateTime? LastSyncedAt { get; set; }
}

/// <summary>Answer of <c>POST /api/channels/{id}/templates/sync</c>.</summary>
public class TemplateSyncResponse
{
    public int Added { get; set; }

    public int Refreshed { get; set; }

    public int Removed { get; set; }

    /// <summary>Templates the list did not have but were kept, because the list was empty or incomplete (docs/MESSAGING.md, "Templates").</summary>
    public int RemovalsHeld { get; set; }

    public bool Sandbox { get; set; }

    public DateTime SyncedAt { get; set; }
}
