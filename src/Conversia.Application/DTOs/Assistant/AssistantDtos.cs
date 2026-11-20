using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Assistant;

/// <summary>The company's assistant settings as <c>GET /api/assistant/settings</c> answers them (docs/ASSISTANT.md, "Settings").</summary>
public class AssistantSettingsResponse
{
    public bool Enabled { get; set; }

    /// <summary>The terms the owner accepted (kept when the assistant is switched off: the consent evidence).</summary>
    public string? TermsVersion { get; set; }

    public DateTime? TermsAcceptedAt { get; set; }

    public Guid? TermsAcceptedBy { get; set; }

    /// <summary>The version of the terms the owner must accept to switch it on now.</summary>
    public string CurrentTermsVersion { get; set; } = string.Empty;

    public int ReplyDelaySeconds { get; set; }

    public string ActiveHours { get; set; } = string.Empty;

    /// <summary>Local <c>HH:mm</c> (America/Bogota), with <c>Custom</c> only.</summary>
    public string? CustomStart { get; set; }

    public string? CustomEnd { get; set; }

    public int MaxAssistantTurns { get; set; }

    public int HumanCooldownMinutes { get; set; }

    public string DisclosureMode { get; set; } = string.Empty;

    public string DisclosureText { get; set; } = string.Empty;

    public string HandoffLine { get; set; } = string.Empty;

    public IReadOnlyList<string> HandoffKeywords { get; set; } = [];

    public string? ToneNotes { get; set; }

    public string Language { get; set; } = AssistantSettings.SupportedLanguage;

    public IReadOnlyList<AssistantChannelResponse> Channels { get; set; } = [];

    /// <summary>The administrator stopped the assistant for this company.</summary>
    public bool BlockedByPlatform { get; set; }

    /// <summary>The administrator stopped the assistant for every company.</summary>
    public bool GloballyEnabled { get; set; }

    /// <summary>The administrator's AI switch of the company (the assistant needs it).</summary>
    public bool AiEnabled { get; set; }

    public int? PublishedKnowledgeNumber { get; set; }

    /// <summary>What really answers today: <c>Null</c> (unit 5a: every fired timer becomes a handoff) or <c>Model</c>.</summary>
    public string Generator { get; set; } = "Null";

    public static string? Clock(int? minute) => minute is { } m ? $"{m / 60:00}:{m % 60:00}" : null;

    /// <summary>Parses <c>HH:mm</c> into a minute of the day; null for blank, -1 for something that is not a time (the domain refuses it).</summary>
    public static int? Minute(string? clock)
    {
        if (string.IsNullOrWhiteSpace(clock))
        {
            return null;
        }

        return TimeOnly.TryParseExact(clock.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.Hour * 60 + time.Minute
            : -1;
    }
}

public class AssistantChannelResponse
{
    public Guid ChannelId { get; set; }

    public string DisplayPhoneNumber { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool AssistantEnabled { get; set; }
}

/// <summary>Body of <c>PUT /api/assistant/settings</c>: every setting but the switch (validated by the domain, Spanish messages).</summary>
public class UpdateAssistantSettingsRequest
{
    [Display(Name = "Espera antes de responder")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public int? ReplyDelaySeconds { get; set; }

    [Display(Name = "Horario")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(AssistantActiveHours))]
    public string? ActiveHours { get; set; }

    [Display(Name = "Inicio")]
    public string? CustomStart { get; set; }

    [Display(Name = "Fin")]
    public string? CustomEnd { get; set; }

    [Display(Name = "Respuestas seguidas")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public int? MaxAssistantTurns { get; set; }

    [Display(Name = "Pausa después de que escribe una persona")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public int? HumanCooldownMinutes { get; set; }

    [Display(Name = "Aviso de asistente virtual")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(AssistantDisclosureMode))]
    public string? DisclosureMode { get; set; }

    [Display(Name = "Texto del aviso")]
    public string? DisclosureText { get; set; }

    [Display(Name = "Frase de traspaso")]
    public string? HandoffLine { get; set; }

    [Display(Name = "Palabras de traspaso")]
    public List<string>? HandoffKeywords { get; set; }

    [Display(Name = "Notas de tono")]
    public string? ToneNotes { get; set; }

    [Display(Name = "Idioma")]
    public string? Language { get; set; }
}

/// <summary>Body of <c>POST /api/assistant/enable</c> (the owner only).</summary>
public class EnableAssistantRequest
{
    [Display(Name = "Versión de los términos")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public string? TermsVersion { get; set; }

    /// <summary>The owner accepts the assistant terms.</summary>
    public bool AcceptTerms { get; set; }

    /// <summary>The owner confirms the company's legal basis to have an assistant answer its customers and that it never claims to be a person.</summary>
    public bool AcknowledgeLegalBasis { get; set; }
}

public class SetChannelAssistantRequest
{
    [Display(Name = "Asistente en la línea")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? Enabled { get; set; }
}

/// <summary>One saved version of the knowledge base, without its content.</summary>
public class KnowledgeVersionSummary
{
    public int Number { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? PublishedAt { get; set; }

    public Guid? PublishedBy { get; set; }

    public static KnowledgeVersionSummary From(AssistantKnowledgeVersion v) => Fill(new KnowledgeVersionSummary(), v);

    protected static T Fill<T>(T target, AssistantKnowledgeVersion v)
        where T : KnowledgeVersionSummary
    {
        target.Number = v.Number;
        target.Status = v.Status.ToString();
        target.CreatedAt = v.CreatedAt;
        target.CreatedBy = v.CreatedBy;
        target.PublishedAt = v.PublishedAt;
        target.PublishedBy = v.PublishedBy;
        return target;
    }
}

public class KnowledgeVersionResponse : KnowledgeVersionSummary
{
    public AssistantKnowledge Content { get; set; } = new();

    public static KnowledgeVersionResponse From(AssistantKnowledgeVersion v, AssistantKnowledge content)
    {
        var response = Fill(new KnowledgeVersionResponse(), v);
        response.Content = content;
        return response;
    }
}

public class KnowledgeListResponse
{
    public IReadOnlyList<KnowledgeVersionSummary> Versions { get; set; } = [];

    public int? PublishedNumber { get; set; }

    public int? LatestNumber { get; set; }
}

/// <summary>Body of <c>POST /api/assistant/knowledge</c>: the whole knowledge base (it becomes a new draft version).</summary>
public class SaveKnowledgeRequest
{
    [Required(ErrorMessage = ValidationMessages.Required)]
    public AssistantKnowledge? Content { get; set; }
}

/// <summary>One row of the assistant's history (<c>GET /api/assistant/audit</c>).</summary>
public class AssistantAuditEntryResponse
{
    public DateTime At { get; set; }

    public Guid? UserId { get; set; }

    public string Action { get; set; } = string.Empty;

    /// <summary>The JSON object of the change.</summary>
    public string Details { get; set; } = "{}";

    public static AssistantAuditEntryResponse From(AssistantAuditEntry e) => new() { At = e.At, UserId = e.UserId, Action = e.Action, Details = e.Details };
}

/// <summary>The assistant's state in a conversation, for the conversation view and the inbox.</summary>
public class ConversationAssistantResponse
{
    /// <summary><c>Off</c>, <c>Waiting</c>, <c>Active</c>, <c>HandedOff</c>, <c>HumanTookOver</c>.</summary>
    public string State { get; set; } = nameof(AssistantState.Off);

    /// <summary>An instance is preparing the answer right now.</summary>
    public bool Generating { get; set; }

    public DateTime? DueAt { get; set; }

    public string? HandoffReason { get; set; }

    public DateTime? HandedOffAt { get; set; }

    public int AssistantTurns { get; set; }

    public DateTime? HumanHoldUntil { get; set; }

    public bool TakenOverExplicitly { get; set; }

    /// <summary>The company has the assistant on for the conversation's line: take-over and hand-back make sense.</summary>
    public bool Available { get; set; }

    public static ConversationAssistantResponse From(ConversationAssistant? state, bool available, DateTime now) => state is null
        ? new ConversationAssistantResponse { Available = available }
        : new ConversationAssistantResponse
        {
            State = state.State.ToString(),
            Generating = state.IsGenerating(now),
            DueAt = state.DueAt,
            HandoffReason = state.HandoffReason,
            HandedOffAt = state.HandedOffAt,
            AssistantTurns = state.AssistantTurns,
            HumanHoldUntil = state.HumanHoldUntil,
            TakenOverExplicitly = state.State == AssistantState.HumanTookOver && state.TakenOverBy is not null,
            Available = available,
        };
}
