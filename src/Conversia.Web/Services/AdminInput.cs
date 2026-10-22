using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Entities;

namespace Conversia.Web.Services;

/// <summary>The quota form as posted. The number stays text: a value that is not a whole number is refused with a message instead of becoming "0".</summary>
public sealed class QuotaInput : IValidatableObject
{
    [Display(Name = "Cupo de líneas activas")]
    public string? MaxActiveLines { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TryParse(MaxActiveLines, out _))
        {
            yield return new ValidationResult(Tenant.MaxActiveLinesRuleMessage, [nameof(MaxActiveLines)]);
        }
    }

    public SetLineQuotaRequest ToRequest() => new() { MaxActiveLines = TryParse(MaxActiveLines, out var value) ? value : null };

    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        return int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
               && value is >= 0 and <= Tenant.MaxLinesLimit;
    }
}

/// <summary>The confirmation form of a status change: only the optional reason.</summary>
public sealed class StatusReasonInput
{
    [Display(Name = "Motivo")]
    [StringLength(Tenant.StatusReasonMaxLength, ErrorMessage = "El motivo no puede superar los 500 caracteres.")]
    public string? Reason { get; set; }

    public ChangeTenantStatusRequest ToRequest(string status)
        => new() { Status = status, Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim() };
}

/// <summary>The confirmation form of switching a line off: it carries nothing (the page address says which line).</summary>
public sealed class LineActionInput
{
    public string? Confirm { get; set; }
}
