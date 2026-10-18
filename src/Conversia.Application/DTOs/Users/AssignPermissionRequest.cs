using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Users;

public class AssignPermissionRequest
{
    [Display(Name = "Permiso")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumDataType(typeof(Permission), ErrorMessage = ValidationMessages.EnumValue)]
    public Permission Permission { get; set; }
}
