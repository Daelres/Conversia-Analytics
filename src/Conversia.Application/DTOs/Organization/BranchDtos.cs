using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Organization;

/// <summary>Body of <c>POST /api/branches</c>. Carries no tenant: the branch always lands in the caller's tenant.</summary>
public class CreateBranchRequest
{
    [Display(Name = "Nombre")]
    [TrimmedText(Branch.NameMaxLength)]
    public string Name { get; set; } = string.Empty;
}

public class BranchResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public static BranchResponse From(Branch branch) => new()
    {
        Id = branch.Id,
        Name = branch.Name,
        IsActive = branch.IsActive,
        CreatedAt = branch.CreatedAt,
    };
}
