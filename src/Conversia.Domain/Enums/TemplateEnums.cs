namespace Conversia.Domain.Enums;

/// <summary>Meta's category of a message template (docs/MESSAGING.md, "Templates"). Meta charges each template message by its category. Stored by name.</summary>
public enum TemplateCategory
{
    /// <summary>The provider gave a category this version does not know.</summary>
    Unknown = 0,
    Marketing = 1,
    Utility = 2,
    Authentication = 3,
}

/// <summary>Whether a template can be sent, as Meta's review and quality rating left it. Stored by name.</summary>
public enum TemplateStatus
{
    /// <summary>The provider gave a status this version does not know: not sent until a sync says otherwise.</summary>
    Unknown = 0,

    /// <summary>In Meta's review (or in appeal): not sendable yet.</summary>
    Pending = 1,

    /// <summary>Approved and active (whatever its quality rating): the only status that can be sent.</summary>
    Approved = 2,

    Rejected = 3,

    /// <summary>Paused by Meta for low quality.</summary>
    Paused = 4,

    /// <summary>Disabled, archived or deleted: it will not come back by itself.</summary>
    Disabled = 5,
}
