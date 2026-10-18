using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Conversations;

/// <summary>
/// Query string of <c>GET /api/conversations</c>. The enum filters are the member names (<c>Won</c>, <c>Ads</c>, <c>Open</c>), never numbers.
/// <c>From</c>/<c>To</c> are local dates of the tenant's time zone (<c>Metrics:TimeZone</c>) applied to the day the conversation
/// started: <c>From</c> inclusive, <c>To</c> exclusive.
/// </summary>
public class ConversationQuery : IValidatableObject
{
    public const int MaxPageSize = 100;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Sucursal")]
    public Guid? BranchId { get; set; }

    [Display(Name = "Asesor")]
    public Guid? AdvisorId { get; set; }

    /// <summary>Only conversations with no advisor. Cannot be combined with <see cref="AdvisorId"/>.</summary>
    [Display(Name = "Sin asesor")]
    public bool? Unassigned { get; set; }

    [Display(Name = "Etapa")]
    [EnumName(typeof(OpportunityStage))]
    public string? Stage { get; set; }

    [Display(Name = "Origen")]
    [EnumName(typeof(ConversationSource))]
    public string? Source { get; set; }

    [Display(Name = "Estado")]
    [EnumName(typeof(ConversationStatus))]
    public string? Status { get; set; }

    [Display(Name = "Desde")]
    public DateOnly? From { get; set; }

    [Display(Name = "Hasta")]
    public DateOnly? To { get; set; }

    /// <summary>The customer wrote and nobody answered yet.</summary>
    [Display(Name = "Sin responder")]
    public bool? Unanswered { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to && from >= to)
        {
            yield return new ValidationResult("El campo Desde debe ser anterior al campo Hasta.", [nameof(From), nameof(To)]);
        }

        if (AdvisorId is not null && Unassigned == true)
        {
            yield return new ValidationResult("No se puede filtrar por un asesor y por conversaciones sin asesor a la vez.", [nameof(AdvisorId), nameof(Unassigned)]);
        }
    }
}

/// <summary>The WhatsApp customer of a conversation. Their number and name are the tenant's own data.</summary>
public class ContactSummary
{
    public Guid Id { get; set; }

    public string WaId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

public class ConversationListItem
{
    public Guid Id { get; set; }

    public ContactSummary Contact { get; set; } = new();

    public Guid ChannelId { get; set; }

    public Guid BranchId { get; set; }

    public string? BranchName { get; set; }

    public Guid? AdvisorId { get; set; }

    public string? AdvisorName { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? SourceReference { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime LastMessageAt { get; set; }

    public DateTime? FirstInboundAt { get; set; }

    public DateTime? FirstResponseAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>The customer wrote and nobody has answered yet.</summary>
    public bool Unanswered => FirstInboundAt is not null && FirstResponseAt is null;

    public int MessageCount { get; set; }

    public OpportunityResponse Opportunity { get; set; } = new();
}

public class ConversationDetailResponse : ConversationListItem
{
    /// <summary>Oldest first, at most <see cref="MaxMessages"/>; <see cref="ConversationListItem.MessageCount"/> says how many exist.</summary>
    public IReadOnlyList<MessageResponse> Messages { get; set; } = [];

    public const int MaxMessages = 2000;
}

public class MessageResponse
{
    public Guid Id { get; set; }

    /// <summary><c>Inbound</c> (the customer) or <c>Outbound</c> (the business).</summary>
    public string Direction { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>Null for media without caption.</summary>
    public string? Text { get; set; }

    public DateTime SentAt { get; set; }
}

public class OpportunityResponse
{
    public string Stage { get; set; } = string.Empty;

    /// <summary><c>Auto</c> (the system decided) or <c>Manual</c> (a person did and the classifier no longer changes it).</summary>
    public string StageSource { get; set; } = string.Empty;

    public DateTime? StageChangedAt { get; set; }

    public string? ServiceInterest { get; set; }

    public string? InterestReason { get; set; }

    public string Objection { get; set; } = string.Empty;

    public string? LossReason { get; set; }

    public string? LossDetail { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime? ClassifiedAt { get; set; }

    public static OpportunityResponse From(Opportunity opportunity) => new()
    {
        Stage = opportunity.Stage.ToString(),
        StageSource = opportunity.StageSource.ToString(),
        StageChangedAt = opportunity.StageChangedAt,
        ServiceInterest = opportunity.ServiceInterest,
        InterestReason = opportunity.InterestReason,
        Objection = opportunity.Objection.ToString(),
        LossReason = opportunity.LossReason?.ToString(),
        LossDetail = opportunity.LossDetail,
        ClosedAt = opportunity.ClosedAt,
        ClassifiedAt = opportunity.ClassifiedAt,
    };
}

/// <summary>Body of <c>PUT /api/conversations/{id}/advisor</c>: the advisor to assign, or null to leave the conversation unassigned.</summary>
public class AssignAdvisorRequest
{
    [Display(Name = "Asesor")]
    public Guid? AdvisorId { get; set; }
}

/// <summary>
/// Body of <c>PUT /api/conversations/{id}/opportunity</c>: a person decides the outcome. <c>Stage</c> is <c>Won</c>, <c>Lost</c> or
/// <c>InFollowUp</c>; <c>Lost</c> requires <c>LossReason</c> (a <see cref="Domain.Enums.LossReason"/> name); <c>Detail</c> is optional free text for a loss.
/// </summary>
public class SetOpportunityOutcomeRequest
{
    [Display(Name = "Etapa")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(OpportunityStage))]
    public string? Stage { get; set; }

    [Display(Name = "Motivo de pérdida")]
    [EnumName(typeof(LossReason))]
    public string? LossReason { get; set; }

    [Display(Name = "Detalle")]
    [TrimmedText(Opportunity.DetailMaxLength, Required = false)]
    public string? Detail { get; set; }
}
