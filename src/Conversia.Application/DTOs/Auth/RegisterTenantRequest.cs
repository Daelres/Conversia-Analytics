using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Auth;

public class RegisterTenantRequest
{
    [Display(Name = "Nombre del espacio de trabajo")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [MaxLength(200, ErrorMessage = ValidationMessages.MaxLength)]
    [NoNullCharacters]
    public string TenantName { get; set; } = string.Empty;

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
