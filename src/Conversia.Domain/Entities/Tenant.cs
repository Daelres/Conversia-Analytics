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

    /// <summary>The platform administrator who last changed the status; null while the company is as it registered, or when the system changed it.</summary>
    public Guid? StatusChangedBy { get; private set; }

    public string? StatusReason { get; private set; }

    /// <summary>The stable code of the last change made by the system (<see cref="ReasonCodes"/>), or given with it; null otherwise.</summary>
    public string? StatusReasonCode { get; private set; }

    /// <summary>Why the company is suspended; null in every other status.</summary>
    public SuspensionCause? SuspensionCause { get; private set; }

    /// <summary>When it was cancelled; null unless it is <see cref="TenantStatus.Cancelled"/>.</summary>
    public DateTime? CancelledAt { get; private set; }

    /// <summary>When the deletion of its data is due (the cancellation plus the retention window); null unless cancelled.</summary>
    public DateTime? DataDeletionDueAt { get; private set; }

    /// <summary>
    /// A late payment noted while the company was paused (the billing job does not suspend a paused company): resuming the pause lands in the
    /// billing suspension while it is set. Cleared by the payment and by a cancellation.
    /// </summary>
    public DateTime? BillingHoldSince { get; private set; }

    /// <summary>When the deletion of its data started; from then on it cannot be reactivated.</summary>
    public DateTime? DataDeletionStartedAt { get; private set; }

    public const int MinDeletionRetentionDays = 1;

    public const int MaxDeletionRetentionDays = 365;

    public const int DefaultDeletionRetentionDays = 30;

    public static readonly string DeletionRetentionRuleMessage =
        $"Los días de conservación antes de eliminar los datos deben estar entre {MinDeletionRetentionDays} y {MaxDeletionRetentionDays}.";

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

    /// <summary>When the LLM reads the company's conversations (<see cref="Enums.AiAnalysisMode"/>). Default: nightly.</summary>
    public AiAnalysisMode AiAnalysisMode { get; private set; } = AiAnalysisMode.Nightly;

    /// <summary>With <see cref="AiAnalysisMode.Nightly"/>: the local hour (0 to 23) the night window opens. Null: the deployment default.</summary>
    public int? AiAnalysisNightlyHour { get; private set; }

    /// <summary>With <see cref="AiAnalysisMode.EveryHours"/>: the hours between windows (1 to 24).</summary>
    public int? AiAnalysisIntervalHours { get; private set; }

    public const int MaxAiAnalysisIntervalHours = 24;

    public static readonly string AiAnalysisScheduleRuleMessage =
        "La frecuencia del análisis con IA no es válida: «cada noche» admite una hora de inicio entre 0 y 23 (o vacía para la hora por defecto), " +
        $"«cada N horas» exige N entre 1 y {MaxAiAnalysisIntervalHours} y «casi en tiempo real» no admite hora ni intervalo.";

    /// <summary>
    /// Sets when the LLM reads the company's conversations. Nightly takes an optional start hour (0 to 23; null = the deployment default) and no
    /// interval; EveryHours takes an interval of 1 to 24 hours and no hour; NearRealTime takes neither. Validated before anything is assigned.
    /// Returns false, and changes nothing, when the schedule already was that one.
    /// </summary>
    public bool SetAiAnalysisSchedule(AiAnalysisMode mode, int? nightlyHour, int? intervalHours)
    {
        var valid = mode switch
        {
            AiAnalysisMode.Nightly => nightlyHour is null or (>= 0 and <= 23) && intervalHours is null,
            AiAnalysisMode.EveryHours => nightlyHour is null && intervalHours is >= 1 and <= MaxAiAnalysisIntervalHours,
            AiAnalysisMode.NearRealTime => nightlyHour is null && intervalHours is null,
            _ => false,
        };
        if (!valid)
        {
            throw new DomainException(AiAnalysisScheduleRuleMessage);
        }

        if (AiAnalysisMode == mode && AiAnalysisNightlyHour == nightlyHour && AiAnalysisIntervalHours == intervalHours)
        {
            return false;
        }

        AiAnalysisMode = mode;
        AiAnalysisNightlyHour = nightlyHour;
        AiAnalysisIntervalHours = intervalHours;
        return true;
    }

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
    /// The transitions an administrator may make (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): Pending to Active, Suspended or
    /// Cancelled; Active to Paused, Suspended or Cancelled; Paused to Active, Suspended or Cancelled; Suspended to Active or Cancelled;
    /// Cancelled to Active (only before its data deletion started, see <see cref="CanReactivate"/>). Staying where it is, and going back to
    /// Pending, are not transitions. Cancelling goes through <see cref="Cancel"/>, which schedules the deletion.
    /// </summary>
    public static bool CanChangeStatus(TenantStatus from, TenantStatus to)
        => (from, to) switch
        {
            (TenantStatus.Pending, TenantStatus.Active or TenantStatus.Suspended or TenantStatus.Cancelled) => true,
            (TenantStatus.Active, TenantStatus.Paused or TenantStatus.Suspended or TenantStatus.Cancelled) => true,
            (TenantStatus.Paused, TenantStatus.Active or TenantStatus.Suspended or TenantStatus.Cancelled) => true,
            (TenantStatus.Suspended, TenantStatus.Active or TenantStatus.Cancelled) => true,
            (TenantStatus.Cancelled, TenantStatus.Active) => true,
            _ => false,
        };

    /// <summary>Like <see cref="CanChangeStatus(TenantStatus, TenantStatus)"/>, with the cause of a suspension (a Billing one can be made Manual).</summary>
    public static bool CanChangeStatus(TenantStatus from, SuspensionCause? cause, TenantStatus to)
        => CanChangeStatus(from, to) || (from, cause, to) == (TenantStatus.Suspended, Enums.SuspensionCause.Billing, TenantStatus.Suspended);

    /// <summary>False only for a cancelled company whose data deletion already started: from then on the data is going, so it cannot come back.</summary>
    public static bool CanReactivate(Tenant tenant) => tenant.Status != TenantStatus.Cancelled || tenant.DataDeletionStartedAt is null;

    /// <summary>The statuses whose webhooks are always accepted. With <see cref="ReceivingSuspensionCause"/>, the one definition of who receives.</summary>
    public static readonly IReadOnlyList<TenantStatus> StatusesThatReceive = [TenantStatus.Active, TenantStatus.Paused];

    /// <summary>The one cause of suspension that keeps receiving: a late payment loses no message.</summary>
    public const SuspensionCause ReceivingSuspensionCause = Enums.SuspensionCause.Billing;

    /// <summary>
    /// Whether the webhooks of a company in this status are accepted and stored: Active, Paused, and Suspended for a late payment. The queries
    /// (webhook lookup, webhook worker, auto-closer) are generated from <see cref="StatusesThatReceive"/> and <see cref="ReceivingSuspensionCause"/>.
    /// </summary>
    public static bool AcceptsWebhooksIn(TenantStatus status, SuspensionCause? cause)
        => StatusesThatReceive.Contains(status) || (status == TenantStatus.Suspended && cause == ReceivingSuspensionCause);

    /// <summary>Its users work, its conversations are analysed and its messages can be sent: Active only.</summary>
    public bool IsOperational => Status == TenantStatus.Active;

    /// <summary>Its webhooks are accepted and its deliveries are stored (no message is lost): see <see cref="AcceptsWebhooksIn"/>.</summary>
    public bool AcceptsWebhooks => AcceptsWebhooksIn(Status, SuspensionCause);

    /// <summary>
    /// Moves the company to <paramref name="newStatus"/> on behalf of a platform administrator. A suspension made this way is
    /// <see cref="Enums.SuspensionCause.Manual"/>. Validates before it assigns anything. Cancelling is <see cref="Cancel"/>.
    /// </summary>
    public void ChangeStatus(TenantStatus newStatus, Guid administratorId, string? reason, DateTime now)
    {
        if (newStatus == TenantStatus.Cancelled)
        {
            throw new DomainException("La cancelación de una empresa programa la eliminación de sus datos: usa la cancelación.");
        }

        if (!CanChangeStatus(Status, SuspensionCause, newStatus))
        {
            throw new DomainException($"No se puede pasar una empresa de {Status} a {newStatus}.");
        }

        if (!CanReactivate(this))
        {
            throw new DomainException(DeletionStartedMessage);
        }

        var cleanReason = TextRules.Optional(reason, StatusReasonMaxLength, StatusReasonRuleMessage);
        var admin = TextRules.RequiredId(administratorId, "El administrador es obligatorio.");
        if (Status == TenantStatus.Paused && newStatus == TenantStatus.Active && BillingHoldSince is not null)
        {
            // Resuming a pause whose bill is still unpaid lands where the payment would have put it.
            SetStatus(TenantStatus.Suspended, Enums.SuspensionCause.Billing, admin, cleanReason, ReasonCodes.BillingPastDue, now);
            BillingHoldSince = null;
            return;
        }

        SetStatus(newStatus, newStatus == TenantStatus.Suspended ? Enums.SuspensionCause.Manual : null, admin, cleanReason, null, now);
        if (newStatus != TenantStatus.Paused)
        {
            BillingHoldSince = null;
        }
    }

    public const string DeletionStartedMessage = "La eliminación de los datos de esta empresa ya empezó: no se puede reactivar.";

    /// <summary>
    /// Cancels the company: nothing of it is received any more and its data is deleted <paramref name="retentionDays"/> days from now,
    /// unless it is reactivated before. <paramref name="actorId"/> is the administrator, or null for the system (billing), which must
    /// then give its <paramref name="reasonCode"/>. Switching its lines off and releasing its licences is the caller's (under the company lock).
    /// </summary>
    public void Cancel(Guid? actorId, string? reason, string? reasonCode, DateTime now, int retentionDays)
    {
        if (retentionDays is < MinDeletionRetentionDays or > MaxDeletionRetentionDays)
        {
            throw new DomainException(DeletionRetentionRuleMessage);
        }

        if (!CanChangeStatus(Status, TenantStatus.Cancelled))
        {
            throw new DomainException($"No se puede pasar una empresa de {Status} a {TenantStatus.Cancelled}.");
        }

        var cleanReason = TextRules.Optional(reason, StatusReasonMaxLength, StatusReasonRuleMessage);
        var code = actorId is null ? ReasonCodes.Required(reasonCode) : ReasonCodes.Optional(reasonCode);
        var actor = actorId is { } id ? TextRules.RequiredId(id, "El administrador es obligatorio.") : (Guid?)null;
        var at = UtcTime.Normalize(now);
        SetStatus(TenantStatus.Cancelled, null, actor, cleanReason, code, at);
        BillingHoldSince = null;
        CancelledAt = at;
        DataDeletionDueAt = at.AddDays(retentionDays);
        DataDeletionStartedAt = null;
    }

    /// <summary>The billing job: an Active company with a late payment is suspended (it keeps receiving its messages). Idempotent.</summary>
    public TenantLifecycleOutcome SuspendForBilling(string reasonCode, DateTime now)
    {
        var code = ReasonCodes.Required(reasonCode);
        if (Status == TenantStatus.Suspended && SuspensionCause == Enums.SuspensionCause.Billing)
        {
            return TenantLifecycleOutcome.Unchanged;
        }

        if (Status == TenantStatus.Paused)
        {
            // Not suspended (the pause was agreed and keeps its words for the users): the hold makes the resume land in the suspension.
            if (BillingHoldSince is not null)
            {
                return TenantLifecycleOutcome.Unchanged;
            }

            BillingHoldSince = UtcTime.Normalize(now);
            StatusReasonCode = code;
            return TenantLifecycleOutcome.Changed;
        }

        if (Status != TenantStatus.Active)
        {
            return TenantLifecycleOutcome.NotApplicable;
        }

        SetStatus(TenantStatus.Suspended, Enums.SuspensionCause.Billing, null, null, code, now);
        return TenantLifecycleOutcome.Changed;
    }

    /// <summary>The billing job: a billing suspension is lifted once paid. Never an administrator's suspension, never a cancellation. Idempotent.</summary>
    public TenantLifecycleOutcome ResumeFromBilling(string reasonCode, DateTime now)
    {
        var code = ReasonCodes.Required(reasonCode);
        if (Status == TenantStatus.Active)
        {
            return TenantLifecycleOutcome.Unchanged;
        }

        if (Status == TenantStatus.Paused)
        {
            if (BillingHoldSince is null)
            {
                return TenantLifecycleOutcome.Unchanged;
            }

            BillingHoldSince = null;
            StatusReasonCode = code;
            return TenantLifecycleOutcome.Changed;
        }

        if (Status != TenantStatus.Suspended || SuspensionCause != Enums.SuspensionCause.Billing)
        {
            return TenantLifecycleOutcome.NotApplicable;
        }

        SetStatus(TenantStatus.Active, null, null, null, code, now);
        return TenantLifecycleOutcome.Changed;
    }

    /// <summary>The billing job: cancels a company that never paid (Pending ones were never billed). Idempotent.</summary>
    public TenantLifecycleOutcome CancelForNonPayment(string reasonCode, DateTime now, int retentionDays)
    {
        var code = ReasonCodes.Required(reasonCode);
        if (Status == TenantStatus.Cancelled)
        {
            return TenantLifecycleOutcome.Unchanged;
        }

        if (Status == TenantStatus.Pending)
        {
            return TenantLifecycleOutcome.NotApplicable;
        }

        Cancel(null, null, code, now, retentionDays);
        return TenantLifecycleOutcome.Changed;
    }

    /// <summary>
    /// Marks the start of the deletion of a cancelled company's data. Returns false, and changes nothing, when it had already started (a
    /// resumed deletion). From now on the company cannot be reactivated.
    /// </summary>
    public bool BeginDataDeletion(DateTime now)
    {
        if (Status != TenantStatus.Cancelled)
        {
            throw new DomainException("Solo se pueden eliminar los datos de una empresa cancelada.");
        }

        if (DataDeletionStartedAt is not null)
        {
            return false;
        }

        DataDeletionStartedAt = UtcTime.Normalize(now);
        return true;
    }

    /// <summary>A cancelled company whose retention window passed, or whose deletion already started (it must be finished).</summary>
    public bool IsDataDeletionDue(DateTime now)
        => Status == TenantStatus.Cancelled && (DataDeletionStartedAt is not null || DataDeletionDueAt <= UtcTime.Normalize(now));

    private void SetStatus(TenantStatus status, SuspensionCause? cause, Guid? actor, string? reason, string? reasonCode, DateTime now)
    {
        Status = status;
        SuspensionCause = cause;
        if (status != TenantStatus.Cancelled)
        {
            CancelledAt = null;
            DataDeletionDueAt = null;
        }

        StatusReason = reason;
        StatusReasonCode = reasonCode;
        StatusChangedBy = actor;
        StatusChangedAt = UtcTime.Normalize(now);
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
