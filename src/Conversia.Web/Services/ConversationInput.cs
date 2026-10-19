using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

namespace Conversia.Web.Services;

/// <summary>The advisor form as posted. An empty value is "nobody"; the id stays text so a malformed one is refused instead of becoming a blank.</summary>
public sealed class AssignAdvisorInput
{
    [Display(Name = "Asesor")]
    public string? AdvisorId { get; set; }

    /// <summary>False when the text is neither empty nor an id.</summary>
    public bool IsValid => string.IsNullOrWhiteSpace(AdvisorId) || Guid.TryParse(AdvisorId, out _);

    public AssignAdvisorRequest ToRequest()
        => new() { AdvisorId = Guid.TryParse(AdvisorId, out var id) ? id : null };
}

/// <summary>The manual outcome form as posted: Won, Lost (with its reason) or back to follow-up.</summary>
public sealed class OutcomeInput : IValidatableObject
{
    public static readonly IReadOnlyList<string> Stages = ["Won", "Lost", "InFollowUp"];

    [Display(Name = "Etapa")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public string? Stage { get; set; }

    [Display(Name = "Motivo de pérdida")]
    public string? LossReason { get; set; }

    [Display(Name = "Detalle")]
    [StringLength(Opportunity.DetailMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Detail { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Stage) && !Stages.Contains(Stage))
        {
            yield return new ValidationResult("Elige si la venta se ganó, se perdió o sigue en seguimiento.", [nameof(Stage)]);
        }

        if (Stage == "Lost" && string.IsNullOrWhiteSpace(LossReason))
        {
            yield return new ValidationResult("Elige por qué se perdió la venta.", [nameof(LossReason)]);
        }

        if (Stage != "Lost" && !string.IsNullOrWhiteSpace(LossReason))
        {
            yield return new ValidationResult("El motivo de pérdida solo se indica cuando la venta se perdió.", [nameof(LossReason)]);
        }

        if (!string.IsNullOrWhiteSpace(LossReason) && !ConversationLabels.LossReasons.Contains(LossReason))
        {
            yield return new ValidationResult("Elige un motivo de la lista.", [nameof(LossReason)]);
        }
    }

    public SetOpportunityOutcomeRequest ToRequest() => new()
    {
        Stage = Stage,
        LossReason = Stage == "Lost" ? LossReason : null,
        Detail = Stage == "Lost" && !string.IsNullOrWhiteSpace(Detail) ? Detail.Trim() : null,
    };
}
