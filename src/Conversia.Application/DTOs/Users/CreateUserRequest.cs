using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Users;

/// <summary>
/// Body of <c>POST /api/users</c>. Deliberately carries no tenant or owner
/// information: the new user always lands in the caller's own tenant as a
/// non-owner with no permissions.
/// </summary>
public class CreateUserRequest
{
    [Display(Name = "Correo electrónico")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EmailAddress(ErrorMessage = ValidationMessages.Email)]
    [MaxLength(256, ErrorMessage = ValidationMessages.MaxLength)]
    [NoNullCharacters]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Contraseña")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [StrongPassword]
    public string Password { get; set; } = string.Empty;
}
