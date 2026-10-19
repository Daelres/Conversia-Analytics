using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>
/// The Web's only way to reach Conversia.API. Authenticated calls take the
/// caller's JWT per call; no token is kept on the client between calls.
/// </summary>
public interface IApiClient
{
    Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<AuthResponse>> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<UserProfileResponse>> GetMyProfileAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<UserSummaryResponse>>> GetUsersAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiResult<UserSummaryResponse>> CreateUserAsync(string token, CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<Unit>> AssignPermissionAsync(string token, Guid userId, Permission permission, CancellationToken cancellationToken = default);

    Task<ApiResult<Unit>> RevokePermissionAsync(string token, Guid userId, Permission permission, CancellationToken cancellationToken = default);

    /// <summary><c>GET /api/products?page=&amp;pageSize=&amp;search=</c>; a null or blank <paramref name="search"/> means no filter.</summary>
    Task<ApiResult<PagedResponse<ProductResponse>>> GetProductsAsync(string token, int page, int pageSize, string? search, CancellationToken cancellationToken = default);

    Task<ApiResult<ProductResponse>> GetProductAsync(string token, Guid id, CancellationToken cancellationToken = default);

    Task<ApiResult<ProductResponse>> CreateProductAsync(string token, CreateProductRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/products/{id}</c>; <see cref="UpdateProductRequest.RowVersion"/> is sent back unchanged.</summary>
    Task<ApiResult<ProductResponse>> UpdateProductAsync(string token, Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<Unit>> DeleteProductAsync(string token, Guid id, CancellationToken cancellationToken = default);

    // ---- metrics (MetricsView) ----

    Task<ApiResult<SummaryResponse>> GetMetricsSummaryAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<ConversionResponse>> GetMetricsConversionAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<FirstResponseResponse>> GetMetricsFirstResponseAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<VolumeResponse>> GetMetricsVolumeAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<AlertsResponse>> GetMetricsAlertsAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default);

    // ---- organization (OrganizationView) ----

    Task<ApiResult<IReadOnlyList<BranchResponse>>> GetBranchesAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<AdvisorResponse>>> GetAdvisorsAsync(string token, CancellationToken cancellationToken = default);

    // ---- conversations (ConversationsView; the two writes need ConversationsManage) ----

    Task<ApiResult<PagedResponse<ConversationListItem>>> GetConversationsAsync(string token, ConversationQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<ConversationDetailResponse>> GetConversationAsync(string token, Guid id, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/conversations/{id}/advisor</c>; a null <see cref="AssignAdvisorRequest.AdvisorId"/> leaves the conversation unassigned.</summary>
    Task<ApiResult<ConversationListItem>> AssignAdvisorAsync(string token, Guid id, AssignAdvisorRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/conversations/{id}/opportunity</c>: a person decides the outcome.</summary>
    Task<ApiResult<OpportunityResponse>> SetOpportunityOutcomeAsync(string token, Guid id, SetOpportunityOutcomeRequest request, CancellationToken cancellationToken = default);
}
