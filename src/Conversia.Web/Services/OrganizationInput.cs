using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

namespace Conversia.Web.Services;

/// <summary>The branch form as posted. The rules are the entity's own, so the page and the API say the same thing.</summary>
public sealed class NewBranchInput
{
    [Display(Name = "Nombre")]
    [TrimmedText(Branch.NameMaxLength)]
    public string? Name { get; set; }

    public CreateBranchRequest ToRequest() => new() { Name = Name!.Trim() };
}

/// <summary>The advisor form as posted. The branch stays text: a malformed id is refused instead of becoming "no branch".</summary>
public sealed class NewAdvisorInput : IValidatableObject
{
    [Display(Name = "Nombre")]
    [TrimmedText(Advisor.NameMaxLength)]
    public string? Name { get; set; }

    [Display(Name = "Sucursal")]
    public string? BranchId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Guid.TryParse(BranchId, out _))
        {
            yield return new ValidationResult("Elige la sucursal del asesor.", [nameof(BranchId)]);
        }
    }

    public CreateAdvisorRequest ToRequest() => new() { Name = Name!.Trim(), BranchId = Guid.Parse(BranchId!) };
}

/// <summary>The WhatsApp number form as posted.</summary>
public sealed class NewChannelInput : IValidatableObject
{
    [Display(Name = "Número de teléfono")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [DisplayPhoneNumber]
    public string? DisplayPhoneNumber { get; set; }

    [Display(Name = "Sucursal")]
    public string? BranchId { get; set; }

    [Display(Name = "Identificador del número en el proveedor")]
    [ProviderPhoneNumberId]
    public string? ProviderPhoneNumberId { get; set; }

    /// <summary>Generated when the form is drawn and sent back with it: a form that is sent twice (reload, double click) creates one channel, not two.</summary>
    public string? SubmissionId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Guid.TryParse(BranchId, out _))
        {
            yield return new ValidationResult("Elige la sucursal del número.", [nameof(BranchId)]);
        }
    }

    public CreateChannelRequest ToRequest() => new()
    {
        BranchId = Guid.Parse(BranchId!),
        DisplayPhoneNumber = DisplayPhoneNumber!.Trim(),
        ProviderPhoneNumberId = string.IsNullOrWhiteSpace(ProviderPhoneNumberId) ? null : ProviderPhoneNumberId.Trim(),
    };
}

/// <summary>The confirmation form of a channel action; it carries only the guard against a second submission.</summary>
public sealed class ChannelActionInput
{
    public string? SubmissionId { get; set; }
}
