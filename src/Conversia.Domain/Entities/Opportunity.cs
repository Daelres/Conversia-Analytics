using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The sales opportunity behind a conversation; in phase 1 there is exactly one per conversation. It starts as
/// <see cref="OpportunityStage.New"/>. The stage is decided either by the system (<see cref="StageSource.Auto"/>: the classifier, or the
/// close-by-inactivity rule) or by a person (<see cref="StageSource.Manual"/>); a manual decision is never overwritten by the system.
/// </summary>
public class Opportunity : BaseEntity
{
    public const int ServiceInterestMaxLength = 100;
    public const int InterestReasonMaxLength = 200;
    public const int DetailMaxLength = 500;
    public const int ClassificationRulesMaxLength = 200;

    public static readonly string DetailRuleMessage = $"El detalle {TextRules.RuleText(DetailMaxLength, required: false)}.";

    public const string StageRuleMessage = "La etapa debe ser Won, Lost o InFollowUp.";
    public const string LossReasonRequiredMessage = "El motivo de pérdida es obligatorio cuando la etapa es Lost.";
    public const string LossReasonNotAllowedMessage = "El motivo de pérdida y el detalle solo se admiten cuando la etapa es Lost.";
    public const string LossReasonInvalidMessage = "El motivo de pérdida no es válido.";

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public OpportunityStage Stage { get; private set; } = OpportunityStage.New;

    public StageSource StageSource { get; private set; } = StageSource.Auto;

    /// <summary>When the stage last changed (or was last decided by a person); null while the opportunity is still <see cref="OpportunityStage.New"/>.</summary>
    public DateTime? StageChangedAt { get; private set; }

    /// <summary>
    /// The user who last changed the stage by hand (<see cref="SetManualOutcome"/>); null when the stage was set by the system (the classifier or
    /// the close-by-inactivity rule) or has not changed yet. An id only: the user's name is not copied here.
    /// </summary>
    public Guid? StageChangedBy { get; private set; }

    /// <summary>The service or body zone the customer asked about, as a normalized code (<c>axilas</c>, <c>piernas,bikini</c>).</summary>
    public string? ServiceInterest { get; private set; }

    /// <summary>Why the customer is interested, as a normalized code (<c>precio_promocion</c>).</summary>
    public string? InterestReason { get; private set; }

    public ObjectionType Objection { get; private set; } = ObjectionType.None;

    /// <summary>Set only while the stage is <see cref="OpportunityStage.Lost"/>.</summary>
    public LossReason? LossReason { get; private set; }

    /// <summary>What a person wrote about the loss; only ever set by a manual outcome.</summary>
    public string? LossDetail { get; private set; }

    /// <summary>When the opportunity reached <see cref="OpportunityStage.Won"/> or <see cref="OpportunityStage.Lost"/>; null otherwise.</summary>
    public DateTime? ClosedAt { get; private set; }

    public DateTime? ClassifiedAt { get; private set; }

    /// <summary>How sure the classifier was, 0 to 1. Null until the first classification.</summary>
    public decimal? ClassificationConfidence { get; private set; }

    /// <summary>The codes of the rules behind the last classification (<c>outcome.won.advisor_confirmation,objection.price</c>); never customer text.</summary>
    public string? ClassificationRules { get; private set; }

    // Required by EF Core for materialization.
    private Opportunity()
    {
    }

    public static Opportunity Open(Guid tenantId, Guid conversationId)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            ConversationId = TextRules.RequiredId(conversationId, "La conversación es obligatoria."),
        };

    /// <summary>
    /// A person decides the outcome. It always wins over the system: from now on the classifier leaves the stage and the loss reason alone.
    /// <see cref="OpportunityStage.Lost"/> needs a loss reason (the detail is optional); the other stages take neither. Everything is
    /// validated before anything changes. <paramref name="changedBy"/> is the user who decided (null for a decision without one, such as seed data).
    /// </summary>
    public void SetManualOutcome(OpportunityStage stage, LossReason? lossReason, string? detail, DateTime now, Guid? changedBy = null)
    {
        if (stage is not (OpportunityStage.Won or OpportunityStage.Lost or OpportunityStage.InFollowUp))
        {
            throw new DomainException(StageRuleMessage);
        }

        var hasDetail = !string.IsNullOrWhiteSpace(detail);
        if (stage == OpportunityStage.Lost)
        {
            if (lossReason is null)
            {
                throw new DomainException(LossReasonRequiredMessage);
            }

            if (!Enum.IsDefined(lossReason.Value))
            {
                throw new DomainException(LossReasonInvalidMessage);
            }
        }
        else if (lossReason is not null || hasDetail)
        {
            throw new DomainException(LossReasonNotAllowedMessage);
        }

        var cleanDetail = TextRules.Optional(detail, DetailMaxLength, DetailRuleMessage);
        var at = UtcTime.Normalize(now);

        StageSource = StageSource.Manual;
        StageChangedAt = at;
        StageChangedBy = changedBy;
        ApplyStage(stage, lossReason, at);
        LossDetail = stage == OpportunityStage.Lost ? cleanDetail : null;
    }

    /// <summary>
    /// The classifier's reading of the conversation. The descriptive fields (service, reason, objection) always follow it. The stage follows it
    /// too, except that a <see cref="StageSource.Manual"/> stage and its loss reason are never touched, an undetermined outcome leaves the
    /// stage alone, and a loss for lack of an answer (set when the conversation closed idle) is only replaced by a decisive outcome (Won or
    /// Lost), never by "still in follow-up". Applying the same reading twice changes nothing but <see cref="ClassifiedAt"/>.
    /// </summary>
    public void ApplyClassification(ClassificationUpdate update, DateTime now)
    {
        if (update.Stage is not (null or OpportunityStage.InFollowUp or OpportunityStage.Won or OpportunityStage.Lost))
        {
            throw new DomainException("La etapa de la clasificación debe ser Won, Lost o InFollowUp.");
        }

        if (update.Stage != OpportunityStage.Lost && update.LossReason is not null)
        {
            throw new DomainException("El motivo de pérdida solo aplica cuando la etapa es Lost.");
        }

        if (update.LossReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new DomainException(LossReasonInvalidMessage);
        }

        if (!Enum.IsDefined(update.Objection))
        {
            throw new DomainException("El tipo de objeción no es válido.");
        }

        if (update.Confidence is < 0m or > 1m)
        {
            throw new DomainException("La confianza de la clasificación debe estar entre 0 y 1.");
        }

        var service = NormalizeCode(update.ServiceInterest, ServiceInterestMaxLength, "El servicio de interés");
        var interest = NormalizeCode(update.InterestReason, InterestReasonMaxLength, "El motivo de interés");
        var rules = NormalizeCode(update.RuleCodes, ClassificationRulesMaxLength, "Las reglas de la clasificación");
        var at = UtcTime.Normalize(now);

        ServiceInterest = service;
        InterestReason = interest;
        Objection = update.Objection;
        ClassificationConfidence = Math.Round(update.Confidence, 2);
        ClassificationRules = rules;
        ClassifiedAt = at;

        if (StageSource == StageSource.Manual || update.Stage is not { } stage)
        {
            return;
        }

        if (Stage == OpportunityStage.Lost && LossReason == Enums.LossReason.NoResponse && stage == OpportunityStage.InFollowUp)
        {
            return;
        }

        var lossReason = stage == OpportunityStage.Lost ? update.LossReason ?? Enums.LossReason.Unknown : (LossReason?)null;
        if (Stage != stage)
        {
            StageChangedAt = at;
            StageChangedBy = null; // the system moved it
        }

        StageSource = StageSource.Auto;
        ApplyStage(stage, lossReason, at);
        LossDetail = null;
    }

    // A normalized code: lower-case ASCII letters, digits, '_' and ',' (a list of codes), at most maxLength long. Null or blank means "none".
    private static string? NormalizeCode(string? code, int maxLength, string subject)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Length > maxLength || !trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or ',' or '.'))
        {
            throw new DomainException($"{subject} debe ser un código de hasta {maxLength} caracteres con letras minúsculas, dígitos, '_', '.' o ','.");
        }

        return trimmed;
    }

    /// <summary>
    /// The conversation went silent after the business wrote last: the opportunity is lost for lack of an answer. Does nothing (returns false)
    /// when a person decided the stage or the opportunity is already Won or Lost.
    /// </summary>
    public bool MarkLostForNoResponse(DateTime now)
    {
        if (StageSource == StageSource.Manual || Stage is OpportunityStage.Won or OpportunityStage.Lost)
        {
            return false;
        }

        var at = UtcTime.Normalize(now);
        StageSource = StageSource.Auto;
        StageChangedAt = at;
        StageChangedBy = null;
        ApplyStage(OpportunityStage.Lost, Enums.LossReason.NoResponse, at);
        LossDetail = null;
        return true;
    }

    // Won and Lost are closed outcomes; the loss reason only exists while Lost. A closed outcome keeps the time it was first reached.
    private void ApplyStage(OpportunityStage stage, LossReason? lossReason, DateTime at)
    {
        var closing = stage is OpportunityStage.Won or OpportunityStage.Lost;
        ClosedAt = closing ? (Stage == stage ? ClosedAt ?? at : at) : null;
        Stage = stage;
        LossReason = stage == OpportunityStage.Lost ? lossReason : null;
    }
}

/// <summary>
/// What a classifier concluded, in the terms the opportunity stores. <paramref name="Stage"/> is null when the outcome is undetermined
/// (the stage is left as it is); <paramref name="LossReason"/> goes with <see cref="OpportunityStage.Lost"/> only.
/// </summary>
public sealed record ClassificationUpdate(
    string? ServiceInterest,
    string? InterestReason,
    ObjectionType Objection,
    OpportunityStage? Stage,
    LossReason? LossReason,
    decimal Confidence,
    string? RuleCodes);

