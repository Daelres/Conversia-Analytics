using Conversia.Domain.Common;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Entities;

/// <summary>
/// The sales opportunity behind a conversation; in phase 1 there is exactly one per conversation. It starts as
/// <see cref="OpportunityStage.New"/>; the classification fields are filled by a later slice.
/// </summary>
public class Opportunity : BaseEntity
{
    public const int ServiceInterestMaxLength = 200;
    public const int ReasonMaxLength = 500;

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public OpportunityStage Stage { get; private set; } = OpportunityStage.New;

    public string? ServiceInterest { get; private set; }

    public string? InterestReason { get; private set; }

    public string? Objection { get; private set; }

    public string? LossReason { get; private set; }

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
}
