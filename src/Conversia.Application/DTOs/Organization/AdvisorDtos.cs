using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Organization;

/// <summary>Body of <c>POST /api/advisors</c>. Carries no tenant: the advisor always lands in the caller's tenant.</summary>
public class CreateAdvisorRequest
{
    [Display(Name = "Nombre")]
    [TrimmedText(Advisor.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Sucursal")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? BranchId { get; set; }

    /// <summary>Optional link to an existing user of the same tenant.</summary>
    [Display(Name = "Usuario")]
    public Guid? UserId { get; set; }
}

public class AdvisorResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid BranchId { get; set; }

    public Guid? UserId { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public static AdvisorResponse From(Advisor advisor) => new()
    {
        Id = advisor.Id,
        Name = advisor.Name,
        BranchId = advisor.BranchId,
        UserId = advisor.UserId,
        IsActive = advisor.IsActive,
        CreatedAt = advisor.CreatedAt,
    };
}
