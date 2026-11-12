using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class Tenant : BaseEntity
{
    private const int MaxNameLength = 200;

    public const int MaxLinesLimit = 1000;
    public const int StatusReasonMaxLength = 500;

    public static readonly string StatusReasonRuleMessage = $"El motivo {TextRules.RuleText(StatusReasonMaxLength, required: false)}.";

    public const int SandboxModeReasonMaxLength = 200;

    public static readonly string SandboxModeReasonRuleMessage = $"El motivo {TextRules.RuleText(SandboxModeReasonMaxLength)}.";

    public static readonly string MaxActiveLinesRuleMessage = $"El cupo de líneas debe ser un número entero entre 0 y {MaxLinesLimit}.";

    public string Name { get; private set; } = string.Empty;

    /// <summary>Starts <see cref="TenantStatus.Pending"/>: the platform administrator decides when a company operates.</summary>
    public TenantStatus Status { get; private set; } = TenantStatus.Pending;

    /// <summary>How many WhatsApp lines (channels) the company may have switched on at once, per its commercial agreement. 0 at registration.</summary>
    public int MaxActiveLines { get; private set; }

    public DateTime? StatusChangedAt { get; private set; }

    /// <summary>The platform administrator who last changed the status; null while the company is as it registered.</summary>
    public Guid? StatusChangedBy { get; private set; }

    public string? StatusReason { get; private set; }

    /// <summary>
    /// The internal sandbox (docs/SANDBOX.md): while it is on, nothing of the company goes to the WhatsApp provider, a simulated customer can
    /// write, and the metrics show sandbox data only. Off by default; only a platform administrator changes it.
    /// </summary>
    public bool SandboxMode { get; private set; }

    public const int AiTermsVersionMaxLength = 50;

    public const int AiChangeReasonMaxLength = 200;

    public const int MaxAiMonthlyConversationQuota = 1_000_000;

    public const int MaxSendRateLimitPerMinute = 1_000_000;

    public static readonly string AiTermsVersionRuleMessage =
        $"La versión de los términos aceptados {TextRules.RuleText(AiTermsVersionMaxLength)}. Es obligatoria para activar la IA.";

    public static readonly string AiChangeReasonRuleMessage = $"El motivo {TextRules.RuleText(AiChangeReasonMaxLength)}.";

    public static readonly string AiQuotaRuleMessage =
        $"El cupo mensual de conversaciones analizadas con IA debe ser un número entero entre 0 y {MaxAiMonthlyConversationQuota}, o vacío para usar el valor por defecto.";

    public static readonly string SendRateLimitRuleMessage = SendRateLimitRuleMessageFor(MaxSendRateLimitPerMinute);

    /// <summary>
    /// Whether the conversations of the company may be read by the LLM (docs/CLASSIFICATION.md). Off by default; only the platform
    /// administrator switches it, with a reason and the version of the terms the company accepted (both audited).
    /// </summary>
    public bool AiEnabled { get; private set; }

    /// <summary>The version of the AI terms the company accepted when the AI was last switched on. Kept when it is switched off (evidence).</summary>
    public string? AiTermsVersion { get; private set; }

    public DateTime? AiEnabledChangedAt { get; private set; }

    /// <summary>Distinct conversations the LLM may analyse per month (America/Bogota). Null: the deployment default.</summary>
    public int? AiMonthlyConversationQuota { get; private set; }

    /// <summary>Messages the company's users may send per minute, all together. Null: the deployment default (<c>Messaging:TenantSendRateLimitPerMinute</c>).</summary>
    public int? SendRateLimitPerMinute { get; private set; }

    /// <summary>
    /// Switches the AI on or off. Returns false, and changes nothing, when it already was that way. Switching on needs the terms version
    /// (validated before anything is assigned); switching off keeps the last version accepted.
    /// </summary>
    public bool SetAiEnabled(bool enabled, string? termsVersion, DateTime now)
    {
        var terms = enabled ? TextRules.Required(termsVersion, AiTermsVersionMaxLength, AiTermsVersionRuleMessage) : null;
        // The same state is a no-op, except new terms accepted while the AI is on: that is consent evidence too.
        if (AiEnabled == enabled && (!enabled || string.Equals(terms, AiTermsVersion, StringComparison.Ordinal)))
        {
            return false;
        }

        var at = UtcTime.Normalize(now);
        AiEnabled = enabled;
        AiEnabledChangedAt = at;
        if (enabled)
        {
            AiTermsVersion = terms;
        }

        return true;
    }

    public void SetAiMonthlyConversationQuota(int? quota)
    {
        if (quota is < 0 or > MaxAiMonthlyConversationQuota)
        {
            throw new DomainException(AiQuotaRuleMessage);
        }

        AiMonthlyConversationQuota = quota;
    }

    /// <summary>The rule with the deployment's maximum (<c>Messaging:TenantSendRateLimitMaxPerMinute</c>).</summary>
    public static string SendRateLimitRuleMessageFor(int maxPerMinute)
        => $"El límite de envíos por minuto debe ser un número entero entre 1 y {maxPerMinute}, o vacío para usar el valor por defecto.";

    /// <summary>
    /// Sets the company's own send limit (null: the default). <paramref name="maxPerMinute"/> is the deployment's maximum, itself never above
    /// <see cref="MaxSendRateLimitPerMinute"/>: an administrator cannot give one company the whole provider budget by mistake.
    /// </summary>
    public void SetSendRateLimitPerMinute(int? limit, int maxPerMinute)
    {
        var max = Math.Min(maxPerMinute, MaxSendRateLimitPerMinute);
        if (limit is { } value && (value < 1 || value > max))
        {
            throw new DomainException(SendRateLimitRuleMessageFor(max));
        }

        SendRateLimitPerMinute = limit;
    }

    /// <summary>The reason an administrator gives for changing a company's limit (AI quota, send limit): required, like the AI switch's.</summary>
    public static string LimitChangeReason(string? reason) => TextRules.Required(reason, AiChangeReasonMaxLength, AiChangeReasonRuleMessage);

    /// <summary>The reason an administrator gives for switching the AI: required, trimmed, at most <see cref="AiChangeReasonMaxLength"/> characters.</summary>
    public static string AiChangeReason(string? reason) => TextRules.Required(reason, AiChangeReasonMaxLength, AiChangeReasonRuleMessage);

    // Required by EF Core for materialization.
    private Tenant()
    {
    }

    public static Tenant Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre de la empresa es obligatorio.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new DomainException($"El nombre de la empresa no puede superar los {MaxNameLength} caracteres.");
        }

        if (name.Contains('\0'))
        {
            throw new DomainException("El nombre de la empresa no puede contener caracteres nulos.");
        }

        return new Tenant
        {
            Name = name
        };
    }

    /// <summary>
    /// The transitions an administrator may make: Pending to Active or Suspended, Active to Suspended, Suspended to Active.
    /// Staying where it is, and going back to Pending, are not transitions.
    /// </summary>
    public static bool CanChangeStatus(TenantStatus from, TenantStatus to)
        => (from, to) switch
        {
            (TenantStatus.Pending, TenantStatus.Active) => true,
            (TenantStatus.Pending, TenantStatus.Suspended) => true,
            (TenantStatus.Active, TenantStatus.Suspended) => true,
            (TenantStatus.Suspended, TenantStatus.Active) => true,
            _ => false,
        };

    /// <summary>Moves the company to <paramref name="newStatus"/> on behalf of a platform administrator. Validates before it assigns anything.</summary>
    public void ChangeStatus(TenantStatus newStatus, Guid administratorId, string? reason, DateTime now)
    {
        if (!CanChangeStatus(Status, newStatus))
        {
            throw new DomainException($"No se puede pasar una empresa de {Status} a {newStatus}.");
        }

        var cleanReason = TextRules.Optional(reason, StatusReasonMaxLength, StatusReasonRuleMessage);
        Status = newStatus;
        StatusReason = cleanReason;
        StatusChangedBy = TextRules.RequiredId(administratorId, "El administrador es obligatorio.");
        StatusChangedAt = now;
    }

    /// <summary>Sets the quota of simultaneously active lines. Whether it is below the lines in use is for the caller to check, under the tenant lock.</summary>
    public void SetMaxActiveLines(int maxActiveLines)
    {
        if (maxActiveLines is < 0 or > MaxLinesLimit)
        {
            throw new DomainException(MaxActiveLinesRuleMessage);
        }

        MaxActiveLines = maxActiveLines;
    }

    /// <summary>Switches the internal sandbox on or off. Returns false, and changes nothing, when it already was that way.</summary>
    public bool SetSandboxMode(bool enabled)
    {
        if (SandboxMode == enabled)
        {
            return false;
        }

        SandboxMode = enabled;
        return true;
    }

    /// <summary>The reason an administrator gives for switching the sandbox: required, trimmed, at most <see cref="SandboxModeReasonMaxLength"/> characters.</summary>
    public static string SandboxModeReason(string? reason) => TextRules.Required(reason, SandboxModeReasonMaxLength, SandboxModeReasonRuleMessage);
}
