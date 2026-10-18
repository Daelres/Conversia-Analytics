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

    // Required by EF Core for materialization.
    private Opportunity()
    {
    }

    public static Opportunity Open(Guid tenantId, Guid conversationId)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            ConversationId = TextRules.RequiredId(conversationId, "La conversación es obligatoria."),
        };

    /// <summary>
    /// A person decides the outcome. It always wins over the system: from now on the classifier leaves the stage and the loss reason alone.
    /// <see cref="OpportunityStage.Lost"/> needs a loss reason (the detail is optional); the other stages take neither. Everything is
    /// validated before anything changes.
    /// </summary>
    public void SetManualOutcome(OpportunityStage stage, LossReason? lossReason, string? detail, DateTime now)
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
        ApplyStage(stage, lossReason, at);
        LossDetail = stage == OpportunityStage.Lost ? cleanDetail : null;
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
