using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Diagnostics;

/// <summary>
/// Body of <c>POST /api/diagnostics/errors</c>: an error of the Web, reported by the Web. It carries no source (always the
/// Web), no fingerprint (computed by the API) and no identity of the row. Texts longer than their column are refused, except
/// <see cref="ExceptionDetails"/>, which is cut.
/// </summary>
public class ReportErrorRequest
{
    /// <summary>A code the Web already showed to its user; honored only when valid and unused.</summary>
    [Display(Name = "Código")]
    public string? Code { get; set; }

    [Display(Name = "Tipo")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(ErrorKind))]
    public string Kind { get; set; } = string.Empty;

    [Display(Name = "Método HTTP")]
    [MaxLength(ErrorLog.HttpMethodMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? HttpMethod { get; set; }

    [Display(Name = "Ruta")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [MaxLength(ErrorLog.PathMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string Path { get; set; } = string.Empty;

    [Display(Name = "Consulta")]
    [MaxLength(ErrorLog.QueryMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Query { get; set; }

    [Display(Name = "Destino")]
    [MaxLength(ErrorLog.EndpointMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Endpoint { get; set; }

    [Display(Name = "Valores de ruta")]
    [MaxLength(ErrorLog.RouteValuesMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? RouteValues { get; set; }

    [Display(Name = "Código de estado")]
    public int? StatusCode { get; set; }

    [Display(Name = "Identificador de traza")]
    [MaxLength(ErrorLog.TraceIdMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? TraceId { get; set; }

    [Display(Name = "Identificador de solicitud")]
    [MaxLength(ErrorLog.RequestIdMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? RequestId { get; set; }

    [Display(Name = "Usuario")]
    public Guid? UserId { get; set; }

    [Display(Name = "Empresa")]
    public Guid? TenantId { get; set; }

    [Display(Name = "Agente de usuario")]
    [MaxLength(ErrorLog.UserAgentMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? UserAgent { get; set; }

    [Display(Name = "Tipo de excepción")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [MaxLength(ErrorLog.ExceptionTypeMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string ExceptionType { get; set; } = string.Empty;

    [Display(Name = "Mensaje de la excepción")]
    [MaxLength(ErrorLog.ExceptionMessageMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? ExceptionMessage { get; set; }

    /// <summary>The full <c>exception.ToString()</c>; the API sanitizes it and keeps its first 16 000 characters.</summary>
    public string? ExceptionDetails { get; set; }

    [Display(Name = "Contexto")]
    [MaxLength(ErrorLog.ContextMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Context { get; set; }

    [Display(Name = "Entorno")]
    [MaxLength(ErrorLog.EnvironmentMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Environment { get; set; }

    [Display(Name = "Versión del servicio")]
    [MaxLength(ErrorLog.ServiceVersionMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? ServiceVersion { get; set; }

    [Display(Name = "Instancia")]
    [MaxLength(ErrorLog.InstanceMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Instance { get; set; }

    [Display(Name = "Fecha y hora")]
    public DateTime? OccurredAt { get; set; }
}
