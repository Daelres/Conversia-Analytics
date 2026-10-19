using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class ConversationService : IConversationService
{
    public const string UnknownAdvisorMessage = "El asesor indicado no existe.";
    public const string AdvisorOtherBranchMessage = "El asesor no trabaja en la sucursal de la conversación.";
    public const string AdvisorInactiveMessage = "El asesor indicado está inactivo.";

    private readonly IConversationQueries _queries;
    private readonly IConversationRepository _conversations;
    private readonly IAdvisorRepository _advisors;
    private readonly TenantTimeZone _timeZone;
    private readonly TimeProvider _timeProvider;

    public ConversationService(
        IConversationQueries queries, IConversationRepository conversations, IAdvisorRepository advisors, TenantTimeZone timeZone, TimeProvider timeProvider)
    {
        _queries = queries;
        _conversations = conversations;
        _advisors = advisors;
        _timeZone = timeZone;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResponse<ConversationListItem>> ListAsync(ConversationQuery query, CancellationToken cancellationToken = default)
    {
        var filter = new ConversationFilter(
            query.Page,
            query.PageSize,
            query.BranchId,
            query.AdvisorId,
            query.Unassigned == true,
            Parse<OpportunityStage>(query.Stage),
            Parse<ConversationSource>(query.Source),
            Parse<ConversationStatus>(query.Status),
            query.From is { } from ? _timeZone.StartOfDayUtc(from) : null,
            query.To is { } to ? _timeZone.StartOfDayUtc(to) : null,
            query.Unanswered == true,
            query.ClosedUnanswered == true);

        var (items, totalCount) = await _queries.ListAsync(filter, cancellationToken);
        return new PagedResponse<ConversationListItem>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<ConversationDetailResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => await _queries.GetDetailAsync(id, cancellationToken) ?? throw new ConversationNotFoundException();

    public async Task<ConversationListItem> AssignAdvisorAsync(Guid id, AssignAdvisorRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetForUpdateAsync(id, cancellationToken) ?? throw new ConversationNotFoundException();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (request.AdvisorId is { } advisorId)
        {
            // An advisor of another tenant is invisible through the query filter, so it fails exactly like an unknown id.
            var advisor = await _advisors.GetByIdAsync(advisorId, cancellationToken) ?? throw new DomainException(UnknownAdvisorMessage);
            if (advisor.BranchId != conversation.BranchId)
            {
                throw new DomainException(AdvisorOtherBranchMessage);
            }

            if (!advisor.IsActive)
            {
                throw new DomainException(AdvisorInactiveMessage);
            }

            conversation.AssignAdvisor(advisorId, actingUserId, now);
        }
        else
        {
            conversation.UnassignAdvisor(actingUserId, now);
        }

        await _conversations.UpdateAsync(conversation, cancellationToken);
        return await _queries.GetSummaryAsync(id, cancellationToken) ?? throw new ConversationNotFoundException();
    }

    public async Task<OpportunityResponse> SetOutcomeAsync(Guid id, SetOpportunityOutcomeRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default)
    {
        var stage = Parse<OpportunityStage>(request.Stage) ?? throw new DomainException("La etapa es obligatoria.");
        var lossReason = Parse<LossReason>(request.LossReason);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var opportunity = await _conversations.ChangeOpportunityAsync(
            id, (_, opportunity) => opportunity.SetManualOutcome(stage, lossReason, request.Detail, now, actingUserId), cancellationToken)
            ?? throw new ConversationNotFoundException();
        return OpportunityResponse.From(opportunity);
    }

    // The DTO attribute already refused anything that is not a member name; blank means "not given".
    private static T? Parse<T>(string? name)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(name) ? null : Enum.Parse<T>(name.Trim(), ignoreCase: true);
}
