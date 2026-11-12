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

    /// <summary>One of <see cref="Conversia.Domain.Common.AdminReasonCodes.All"/>, or empty.</summary>
    public string? ReasonCode { get; set; }

    public ChangeTenantStatusRequest ToRequest(string status)
        => new()
        {
            Status = status,
            Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(),
            ReasonCode = string.IsNullOrWhiteSpace(ReasonCode) ? null : ReasonCode.Trim(),
        };
}

/// <summary>The form of deleting a cancelled company's data now: the company's name, typed, and the reason (both required).</summary>
public sealed class DeleteDataInput
{
    [Display(Name = "Nombre de la empresa")]
    [Required(ErrorMessage = "Escribe el nombre de la empresa para confirmar.")]
    [StringLength(200, ErrorMessage = "El nombre no puede superar los 200 caracteres.")]
    public string? ConfirmName { get; set; }

    [Display(Name = "Motivo")]
    [Required(ErrorMessage = "El motivo es obligatorio.")]
    [StringLength(DeleteTenantDataRequest.ReasonMaxLength, ErrorMessage = "El motivo no puede superar los 200 caracteres.")]
    public string? Reason { get; set; }

    public DeleteTenantDataRequest ToRequest() => new() { ConfirmName = ConfirmName?.Trim(), Reason = Reason?.Trim() };
}

/// <summary>The confirmation form of switching a line off: it carries nothing (the page address says which line).</summary>
public sealed class LineActionInput
{
    public string? Confirm { get; set; }
}

/// <summary>The confirmation form of switching the sandbox mode of a company: the reason, which is required and audited.</summary>
public sealed class SandboxModeInput
{
    [Display(Name = "Motivo")]
    [Conversia.Application.Validation.TrimmedText(Tenant.SandboxModeReasonMaxLength)]
    public string? Reason { get; set; }

    public SetSandboxModeRequest ToRequest(bool enabled) => new() { Enabled = enabled, Reason = Reason?.Trim() };
}

/// <summary>
/// An optional whole number posted as text (a limit): empty means "the default", anything else must be a whole number in range. The value is
/// kept as text so a typo is refused with a message instead of becoming the default.
/// </summary>
public static class OptionalWholeNumber
{
    public static bool TryParse(string? text, int min, int max, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= min && parsed <= max)
        {
            value = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>The AI quota form: empty resets to the default.</summary>
public sealed class AiQuotaInput : IValidatableObject
{
    [Display(Name = "Cupo mensual de conversaciones con IA")]
    public string? MonthlyConversations { get; set; }

    [Display(Name = "Motivo")]
    [Conversia.Application.Validation.TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!OptionalWholeNumber.TryParse(MonthlyConversations, 0, Tenant.MaxAiMonthlyConversationQuota, out _))
        {
            yield return new ValidationResult(Tenant.AiQuotaRuleMessage, [nameof(MonthlyConversations)]);
        }
    }

    public SetAiQuotaRequest ToRequest()
        => new()
        {
            MonthlyConversations = OptionalWholeNumber.TryParse(MonthlyConversations, 0, Tenant.MaxAiMonthlyConversationQuota, out var value) ? value : null,
            Reason = Reason?.Trim(),
        };
}

/// <summary>The send rate limit form: empty resets to the default.</summary>
public sealed class SendLimitInput : IValidatableObject
{
    [Display(Name = "Límite de envíos por minuto")]
    public string? PerMinute { get; set; }

    [Display(Name = "Motivo")]
    [Conversia.Application.Validation.TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!OptionalWholeNumber.TryParse(PerMinute, 1, Tenant.MaxSendRateLimitPerMinute, out _))
        {
            yield return new ValidationResult(Tenant.SendRateLimitRuleMessage, [nameof(PerMinute)]);
        }
    }

    public SetSendRateLimitRequest ToRequest()
        => new() { PerMinute = OptionalWholeNumber.TryParse(PerMinute, 1, Tenant.MaxSendRateLimitPerMinute, out var value) ? value : null, Reason = Reason?.Trim() };
}

/// <summary>The confirmation form of switching the AI: the reason (required) and, to switch it on, the version of the accepted terms.</summary>
public sealed class AiSwitchInput
{
    [Display(Name = "Motivo")]
    [Conversia.Application.Validation.TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }

    [Display(Name = "Versión de los términos aceptados")]
    [Conversia.Application.Validation.TrimmedText(Tenant.AiTermsVersionMaxLength, Required = false)]
    public string? TermsVersion { get; set; }

    public SetAiEnabledRequest ToRequest(bool enabled)
        => new() { Enabled = enabled, Reason = Reason?.Trim(), TermsVersion = enabled ? TermsVersion?.Trim() : null };
}

/// <summary>The new licence form. The cost stays text so a typo is refused instead of becoming 0.</summary>
public sealed class LicenseInput : IValidatableObject
{
    [Display(Name = "Número")]
    public string? PhoneNumber { get; set; }

    [Display(Name = "Identificador en 360dialog")]
    public string? ProviderChannelId { get; set; }

    [Display(Name = "Costo mensual (USD)")]
    public string? MonthlyCostUsd { get; set; }

    public bool CustomerOwned { get; set; }

    [Display(Name = "Notas")]
    [StringLength(LineLicense.NotesMaxLength, ErrorMessage = "Las notas no pueden superar los 500 caracteres.")]
    public string? Notes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!WhatsAppChannel.IsValidDisplayPhoneNumber(PhoneNumber))
        {
            yield return new ValidationResult(WhatsAppChannel.DisplayPhoneNumberRuleMessage, [nameof(PhoneNumber)]);
        }

        if (!WhatsAppChannel.IsValidProviderPhoneNumberId(ProviderChannelId))
        {
            yield return new ValidationResult(WhatsAppChannel.ProviderPhoneNumberIdRuleMessage, [nameof(ProviderChannelId)]);
        }

        if (!TryCost(MonthlyCostUsd, out _))
        {
            yield return new ValidationResult(LineLicense.CostRuleMessage, [nameof(MonthlyCostUsd)]);
        }
    }

    public CreateLineLicenseRequest ToRequest() => new()
    {
        PhoneNumber = PhoneNumber?.Trim() ?? string.Empty,
        ProviderChannelId = string.IsNullOrWhiteSpace(ProviderChannelId) ? null : ProviderChannelId.Trim(),
        MonthlyCostUsd = TryCost(MonthlyCostUsd, out var cost) ? cost : null,
        CustomerOwned = CustomerOwned,
        Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
    };

    // "12.50" or "12,50": both are how a person writes it here.
    private static bool TryCost(string? text, out decimal cost)
    {
        cost = 0m;
        return !string.IsNullOrWhiteSpace(text)
               && decimal.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out cost)
               && cost is >= 0m and <= LineLicense.MaxMonthlyCostUsd && decimal.Round(cost, 2) == cost;
    }
}

/// <summary>The assignment form of a licence: the company.</summary>
public sealed class LicenseAssignInput
{
    [Display(Name = "Empresa")]
    [Required(ErrorMessage = "Elige la empresa.")]
    public Guid? TenantId { get; set; }
}

/// <summary>The confirmation forms of releasing or retiring a licence: they carry nothing (the address says which licence).</summary>
public sealed class LicenseActionInput
{
    public string? Confirm { get; set; }
}
