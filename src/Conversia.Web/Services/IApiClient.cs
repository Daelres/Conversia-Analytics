using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.DTOs.Tenants;
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

    /// <summary>
    /// <c>POST /api/conversations/{id}/messages</c>: sends a plain-text WhatsApp message to the customer of the conversation and stores it.
    /// A refusal carries its <see cref="ApiResult{T}.ProblemCode"/> (docs/MESSAGING.md); the API makes one attempt and so does this call.
    /// </summary>
    Task<ApiResult<MessageResponse>> SendMessageAsync(string token, Guid id, SendMessageRequest request, CancellationToken cancellationToken = default);

    // ---- messages and webhook deliveries (ConversationsView) ----

    Task<ApiResult<PagedResponse<RecentMessageItem>>> GetRecentMessagesAsync(string token, RecentMessagesQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<MessagesSummaryResponse>> GetMessagesSummaryAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiResult<PagedResponse<WebhookEventItem>>> GetWebhookEventsAsync(string token, WebhookEventsQuery query, CancellationToken cancellationToken = default);

    // ---- organization writes (OrganizationManage) ----

    Task<ApiResult<IReadOnlyList<ChannelResponse>>> GetChannelsAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiResult<ChannelResponse>> GetChannelAsync(string token, Guid id, CancellationToken cancellationToken = default);

    Task<ApiResult<BranchResponse>> CreateBranchAsync(string token, CreateBranchRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<AdvisorResponse>> CreateAdvisorAsync(string token, CreateAdvisorRequest request, CancellationToken cancellationToken = default);

    /// <summary>The answer carries the webhook secret: the only time it is ever given.</summary>
    Task<ApiResult<CreateChannelResponse>> CreateChannelAsync(string token, CreateChannelRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>POST /api/channels/{id}/activate</c> or <c>/deactivate</c>.</summary>
    Task<ApiResult<ChannelResponse>> SetChannelActiveAsync(string token, Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>The answer carries the NEW webhook secret: the only time it is ever given.</summary>
    Task<ApiResult<CreateChannelResponse>> RotateChannelSecretAsync(string token, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>PUT /api/channels/{id}/provider-credentials</c>: stores the provider API key of the channel (write-only: no answer carries it)
    /// and its environment. A request without a key keeps the stored one and changes only the environment.
    /// </summary>
    Task<ApiResult<ChannelResponse>> SetProviderCredentialsAsync(string token, Guid id, SetProviderCredentialsRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE /api/channels/{id}/provider-credentials</c>: removes the stored key. Idempotent.</summary>
    Task<ApiResult<ChannelResponse>> RemoveProviderCredentialsAsync(string token, Guid id, CancellationToken cancellationToken = default);

    // ---- the internal sandbox of the caller's company (docs/SANDBOX.md) ----

    /// <summary>
    /// <c>POST /api/sandbox/conversations</c>: a simulated customer writes for the first time. The message goes into the ingestion queue,
    /// so the answer names no conversation. A refusal carries its <see cref="ApiResult{T}.ProblemCode"/> (<see cref="SandboxFailure"/>).
    /// </summary>
    Task<ApiResult<SandboxMessageAcceptedResponse>> StartSandboxConversationAsync(string token, StartSandboxConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>POST /api/sandbox/conversations/{id}/customer-messages</c>: the simulated customer of a sandbox conversation writes again.</summary>
    Task<ApiResult<SandboxMessageAcceptedResponse>> SendSandboxCustomerMessageAsync(string token, Guid id, SandboxCustomerMessageRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE /api/sandbox/data</c>: removes every sandbox row of the company, and nothing else.</summary>
    Task<ApiResult<SandboxDataDeletedResponse>> DeleteSandboxDataAsync(string token, CancellationToken cancellationToken = default);

    // ---- the caller's own company (any status) ----

    /// <summary><c>GET /api/tenant</c>: where the company stands (status and line quota). Answered while the company is pending or suspended too.</summary>
    Task<ApiResult<TenantInfoResponse>> GetTenantAsync(string token, CancellationToken cancellationToken = default);

    // ---- platform administration (the administrator's own token; see docs/PLATFORM_ADMIN.md) ----

    /// <summary><c>POST /api/admin/auth/login</c>: the administrator's own sign-in. A 401 is the same whatever was wrong.</summary>
    Task<ApiResult<AdminAuthResponse>> AdminLoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<PagedResponse<TenantSummaryResponse>>> GetAdminTenantsAsync(string token, AdminTenantsQuery query, CancellationToken cancellationToken = default);

    Task<ApiResult<TenantDetailResponse>> GetAdminTenantAsync(string token, Guid id, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/admin/tenants/{id}/status</c>; an invalid transition is a 409 whose message is in <see cref="ApiResult{T}.Detail"/>.</summary>
    Task<ApiResult<TenantSummaryResponse>> ChangeTenantStatusAsync(string token, Guid id, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/admin/tenants/{id}/line-quota</c>; a quota below the active lines is a 409.</summary>
    Task<ApiResult<TenantSummaryResponse>> SetTenantLineQuotaAsync(string token, Guid id, SetLineQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>PUT /api/admin/tenants/{id}/sandbox-mode</c>: switches the internal sandbox of a company on or off, with the reason. Idempotent.</summary>
    Task<ApiResult<TenantSummaryResponse>> SetTenantSandboxModeAsync(string token, Guid id, SetSandboxModeRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>POST /api/admin/tenants/{id}/channels/{channelId}/deactivate</c>: forces a line off. Idempotent.</summary>
    Task<ApiResult<TenantLineResponse>> DeactivateTenantLineAsync(string token, Guid id, Guid channelId, CancellationToken cancellationToken = default);

    Task<ApiResult<PagedResponse<AuditEntryResponse>>> GetAdminAuditAsync(string token, AdminAuditQuery query, CancellationToken cancellationToken = default);
}
