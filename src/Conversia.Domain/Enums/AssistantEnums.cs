namespace Conversia.Domain.Enums;

/// <summary>
/// Where the AI sales assistant stands in one conversation (docs/ASSISTANT.md, "State machine"). Stored by name.
/// </summary>
public enum AssistantState
{
    /// <summary>Nothing armed: the assistant waits for an unanswered customer message.</summary>
    Off = 0,

    /// <summary>A customer message is unanswered and the reply timer is armed (<c>DueAt</c>).</summary>
    Waiting = 1,

    /// <summary>The assistant replied and nobody else has written since.</summary>
    Active = 2,

    /// <summary>The assistant stepped back and left the conversation pending human review (<c>HandoffReason</c>).</summary>
    HandedOff = 3,

    /// <summary>A person wrote (or took the conversation over): the assistant stays quiet until the hold ends or it is handed back.</summary>
    HumanTookOver = 4,
}

/// <summary>When the assistant may answer. Stored by name.</summary>
public enum AssistantActiveHours
{
    /// <summary>At any time.</summary>
    Always = 0,

    /// <summary>Only outside the opening hours of the published knowledge base (and on its holidays).</summary>
    OutsideBusinessHours = 1,

    /// <summary>Only within a daily local window (<c>CustomStartMinute</c> to <c>CustomEndMinute</c>, which may cross midnight).</summary>
    Custom = 2,
}

/// <summary>How the assistant says it is an assistant. Whatever the mode, it never claims to be a person. Stored by name.</summary>
public enum AssistantDisclosureMode
{
    /// <summary>No proactive disclosure (legal review pending: docs/ASSISTANT.md, "Disclosure"). A direct question still gets a truthful answer.</summary>
    None = 0,

    /// <summary>The first assistant message of the conversation starts with the disclosure text.</summary>
    FirstMessage = 1,

    /// <summary>The disclosure text answers a customer who asks whether they are talking to a bot or a person. The default.</summary>
    OnRequest = 2,
}

/// <summary>The life of one version of a company's knowledge base. Stored by name.</summary>
public enum AssistantKnowledgeStatus
{
    /// <summary>Saved, not used by the assistant.</summary>
    Draft = 0,

    /// <summary>The one version the assistant uses (at most one per company).</summary>
    Published = 1,

    /// <summary>Published before, replaced by a newer publication.</summary>
    Archived = 2,
}
