using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Conversations;

namespace Conversia.Application.Interfaces;

/// <summary>Reading the conversations of the caller's tenant, assigning their advisor and deciding their outcome by hand.</summary>
public interface IConversationService
{
    /// <summary>Newest first (by start). Dates in the query are local dates of the tenant's time zone.</summary>
    Task<PagedResponse<ConversationListItem>> ListAsync(ConversationQuery query, CancellationToken cancellationToken = default);

    /// <summary>The conversation with its opportunity and its messages. Throws <see cref="Exceptions.ConversationNotFoundException"/> for a missing or other-tenant id.</summary>
    Task<ConversationDetailResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns the advisor, or unassigns with a null id. The advisor must exist in the tenant, be active and work at the branch of the
    /// conversation, otherwise a <see cref="Domain.Exceptions.DomainException"/> (400). Throws <see cref="Exceptions.ConversationNotFoundException"/>.
    /// <paramref name="actingUserId"/> is the user who does it, recorded on the conversation (<c>AdvisorAssignedBy</c>).
    /// </summary>
    Task<ConversationListItem> AssignAdvisorAsync(Guid id, AssignAdvisorRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A person decides the outcome; the classifier never changes it afterwards. Throws <see cref="Exceptions.ConversationNotFoundException"/>
    /// and <see cref="Domain.Exceptions.DomainException"/> for a stage/reason combination the rules refuse.
    /// <paramref name="actingUserId"/> is the user who decides, recorded on the opportunity (<c>StageChangedBy</c>).
    /// </summary>
    Task<OpportunityResponse> SetOutcomeAsync(Guid id, SetOpportunityOutcomeRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);
}

/// <summary>The conversation list filters, parsed: enums are real enums and dates are UTC instants (<c>FromUtc</c> inclusive, <c>ToUtc</c> exclusive).</summary>
public sealed record ConversationFilter(
    int Page,
    int PageSize,
    Guid? BranchId = null,
    Guid? AdvisorId = null,
    bool Unassigned = false,
    Domain.Enums.OpportunityStage? Stage = null,
    Domain.Enums.ConversationSource? Source = null,
    Domain.Enums.ConversationStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    bool UnansweredOnly = false,
    bool ClosedUnansweredOnly = false);

/// <summary>Read side of the conversations: projections straight to the response shapes, tenant-scoped by the query filters.</summary>
public interface IConversationQueries
{
    Task<(IReadOnlyList<ConversationListItem> Items, int TotalCount)> ListAsync(ConversationFilter filter, CancellationToken cancellationToken = default);

    Task<ConversationListItem?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConversationDetailResponse?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default);
}
