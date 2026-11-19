using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Billing;
using Conversia.Application.DTOs.Contacts;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.DTOs.Tenants;
using Conversia.Application.DTOs.Users;
using System.Net.Sockets;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Web.ErrorReporting;

namespace Conversia.Web.Services;

/// <summary>
/// Typed HttpClient over Conversia.API. Every outcome, including transport and protocol failures,
/// comes back as an <see cref="ApiResult{T}"/>; only the caller's own cancellation propagates. An outcome that means "the service is not
/// available" carries the code of that unexpected error: the API's own when it already registered the failure, otherwise one the Web
/// registers through <see cref="IWebErrorReporter"/>.
/// </summary>
public sealed class ApiClient : IApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    // Each call has its own limit: sending a message waits for the API to call the provider and record the result, far longer than a read.
    private readonly TimeSpan? _timeout;
    private readonly TimeSpan? _sendTimeout;
    private readonly ILogger<ApiClient>? _logger;
    private readonly IWebErrorReporter? _errorReporter;
    private readonly ErrorCodeState? _errorCodes;

    /// <param name="options">
    /// The time limits of the calls. Without them (a client built by hand) the <see cref="HttpClient"/>'s own timeout is the only limit.
    /// </param>
    public ApiClient(HttpClient httpClient, ILogger<ApiClient>? logger = null, IWebErrorReporter? errorReporter = null, ErrorCodeState? errorCodes = null, Microsoft.Extensions.Options.IOptions<ApiOptions>? options = null)
    {
        _timeout = options is null ? null : TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
        _sendTimeout = options is null ? null : TimeSpan.FromSeconds(options.Value.SendTimeoutSeconds);
        _http = httpClient;
        _logger = logger;
        _errorReporter = errorReporter;
        _errorCodes = errorCodes;
    }

    public Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login", token: null, request, HasToken, cancellationToken);

    public Task<ApiResult<AuthResponse>> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/register-tenant", token: null, request, HasToken, cancellationToken);

    public Task<ApiResult<UserProfileResponse>> GetMyProfileAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<UserProfileResponse>(HttpMethod.Get, "api/users/me", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<IReadOnlyList<UserSummaryResponse>>> GetUsersAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<UserSummaryResponse>>(HttpMethod.Get, "api/users", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<UserSummaryResponse>> CreateUserAsync(string token, CreateUserRequest request, CancellationToken cancellationToken = default)
        => SendAsync<UserSummaryResponse>(HttpMethod.Post, "api/users", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<Unit>> AssignPermissionAsync(string token, Guid userId, Permission permission, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, $"api/users/{userId}/permissions", token, new AssignPermissionRequest { Permission = permission }, isUsable: null, cancellationToken);

    public Task<ApiResult<Unit>> RevokePermissionAsync(string token, Guid userId, Permission permission, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Delete, $"api/users/{userId}/permissions/{Uri.EscapeDataString(permission.ToString())}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<AiAnalysisStatusResponse>> GetAiAnalysisStatusAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<AiAnalysisStatusResponse>(HttpMethod.Get, "api/tenant/ai-analysis", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<SummaryResponse>> GetMetricsSummaryAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<SummaryResponse>(HttpMethod.Get, "api/metrics/summary" + MetricsQueryString.Build(query), token, body: null, r => r.Conversion is not null && r.FirstResponse is not null && r.Period is not null, cancellationToken);

    public Task<ApiResult<ConversionResponse>> GetMetricsConversionAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<ConversionResponse>(HttpMethod.Get, "api/metrics/conversion" + MetricsQueryString.Build(query), token, body: null, r => r.Totals is not null && r.ByBranch is not null && r.ByAdvisor is not null && r.BySource is not null, cancellationToken);

    public Task<ApiResult<FirstResponseResponse>> GetMetricsFirstResponseAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<FirstResponseResponse>(HttpMethod.Get, "api/metrics/first-response" + MetricsQueryString.Build(query), token, body: null, r => r.Totals is not null && r.ByBranch is not null && r.ByAdvisor is not null && r.BySource is not null, cancellationToken);

    public Task<ApiResult<VolumeResponse>> GetMetricsVolumeAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<VolumeResponse>(HttpMethod.Get, "api/metrics/volume" + MetricsQueryString.Build(query), token, body: null, r => r.ByDay is not null && r.ByHourOfWeek is not null, cancellationToken);

    public Task<ApiResult<AlertsResponse>> GetMetricsAlertsAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<AlertsResponse>(HttpMethod.Get, "api/metrics/alerts" + MetricsQueryString.Build(query), token, body: null, r => r.Alerts is not null && r.Unanswered is not null, cancellationToken);

    public Task<ApiResult<ClosingPointResponse>> GetMetricsClosingPointAsync(string token, MetricsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<ClosingPointResponse>(HttpMethod.Get, "api/metrics/closing-point" + MetricsQueryString.Build(query), token, body: null, r => r.Stages is not null && r.MovedOffChannel is not null && r.Period is not null, cancellationToken);

    public Task<ApiResult<IReadOnlyList<BranchResponse>>> GetBranchesAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<BranchResponse>>(HttpMethod.Get, "api/branches", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<IReadOnlyList<AdvisorResponse>>> GetAdvisorsAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<AdvisorResponse>>(HttpMethod.Get, "api/advisors", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<PagedResponse<ConversationListItem>>> GetConversationsAsync(string token, ConversationQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<ConversationListItem>>(HttpMethod.Get, "api/conversations" + ConversationsQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<ConversationDetailResponse>> GetConversationAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ConversationDetailResponse>(HttpMethod.Get, $"api/conversations/{id}", token, body: null, response => response.Messages is not null && response.Contact is not null && response.Opportunity is not null, cancellationToken);

    public Task<ApiResult<ConversationListItem>> AssignAdvisorAsync(string token, Guid id, AssignAdvisorRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ConversationListItem>(HttpMethod.Put, $"api/conversations/{id}/advisor", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<OpportunityResponse>> SetOpportunityOutcomeAsync(string token, Guid id, SetOpportunityOutcomeRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OpportunityResponse>(HttpMethod.Put, $"api/conversations/{id}/opportunity", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<MessageResponse>> SendMessageAsync(string token, Guid id, SendMessageRequest request, CancellationToken cancellationToken = default)
        => SendAsync<MessageResponse>(HttpMethod.Post, $"api/conversations/{id}/messages", token, request, isUsable: null, cancellationToken, SendFailure.SendCodes, _sendTimeout);

    public Task<ApiResult<PagedResponse<RecentMessageItem>>> GetRecentMessagesAsync(string token, RecentMessagesQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<RecentMessageItem>>(HttpMethod.Get, "api/messages/recent" + MessagesQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<MessagesSummaryResponse>> GetMessagesSummaryAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<MessagesSummaryResponse>(HttpMethod.Get, "api/messages/summary", token, body: null, response => response.Lines is not null, cancellationToken);

    public Task<ApiResult<PagedResponse<WebhookEventItem>>> GetWebhookEventsAsync(string token, WebhookEventsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<WebhookEventItem>>(HttpMethod.Get, "api/webhook-events" + MessagesQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<IReadOnlyList<ChannelResponse>>> GetChannelsAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ChannelResponse>>(HttpMethod.Get, "api/channels", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ChannelResponse>> GetChannelAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ChannelResponse>(HttpMethod.Get, $"api/channels/{id}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<BranchResponse>> CreateBranchAsync(string token, CreateBranchRequest request, CancellationToken cancellationToken = default)
        => SendAsync<BranchResponse>(HttpMethod.Post, "api/branches", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<AdvisorResponse>> CreateAdvisorAsync(string token, CreateAdvisorRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AdvisorResponse>(HttpMethod.Post, "api/advisors", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<CreateChannelResponse>> CreateChannelAsync(string token, CreateChannelRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CreateChannelResponse>(HttpMethod.Post, "api/channels", token, request, response => !string.IsNullOrEmpty(response.WebhookSecret), cancellationToken);

    public Task<ApiResult<ChannelResponse>> SetChannelActiveAsync(string token, Guid id, bool active, CancellationToken cancellationToken = default)
        => SendAsync<ChannelResponse>(HttpMethod.Post, $"api/channels/{id}/{(active ? "activate" : "deactivate")}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<CreateChannelResponse>> RotateChannelSecretAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CreateChannelResponse>(HttpMethod.Post, $"api/channels/{id}/rotate-secret", token, body: null, response => !string.IsNullOrEmpty(response.WebhookSecret), cancellationToken);

    public Task<ApiResult<ChannelResponse>> SetProviderCredentialsAsync(string token, Guid id, SetProviderCredentialsRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ChannelResponse>(HttpMethod.Put, $"api/channels/{id}/provider-credentials", token, request, isUsable: null, cancellationToken, SendFailure.CredentialCodes);

    public Task<ApiResult<ChannelResponse>> RemoveProviderCredentialsAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ChannelResponse>(HttpMethod.Delete, $"api/channels/{id}/provider-credentials", token, body: null, isUsable: null, cancellationToken, SendFailure.CredentialCodes);

    public Task<ApiResult<SandboxMessageAcceptedResponse>> StartSandboxConversationAsync(string token, StartSandboxConversationRequest request, CancellationToken cancellationToken = default)
        => SendAsync<SandboxMessageAcceptedResponse>(HttpMethod.Post, "api/sandbox/conversations", token, request, response => !string.IsNullOrEmpty(response.CustomerWaId), cancellationToken, SandboxFailure.Codes);

    public Task<ApiResult<SandboxMessageAcceptedResponse>> SendSandboxCustomerMessageAsync(string token, Guid id, SandboxCustomerMessageRequest request, CancellationToken cancellationToken = default)
        => SendAsync<SandboxMessageAcceptedResponse>(HttpMethod.Post, $"api/sandbox/conversations/{id}/customer-messages", token, request, response => !string.IsNullOrEmpty(response.CustomerWaId), cancellationToken, SandboxFailure.Codes);

    public Task<ApiResult<SandboxDataDeletedResponse>> DeleteSandboxDataAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<SandboxDataDeletedResponse>(HttpMethod.Delete, "api/sandbox/data", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantInfoResponse>> GetTenantAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<TenantInfoResponse>(HttpMethod.Get, "api/tenant", token, body: null, response => !string.IsNullOrEmpty(response.Name), cancellationToken);

    public Task<ApiResult<AdminAuthResponse>> AdminLoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AdminAuthResponse>(HttpMethod.Post, "api/admin/auth/login", token: null, request, response => !string.IsNullOrWhiteSpace(response.Token), cancellationToken);

    public Task<ApiResult<PagedResponse<TenantSummaryResponse>>> GetAdminTenantsAsync(string token, AdminTenantsQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<TenantSummaryResponse>>(HttpMethod.Get, "api/admin/tenants" + AdminQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<TenantDetailResponse>> GetAdminTenantAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<TenantDetailResponse>(HttpMethod.Get, $"api/admin/tenants/{id}", token, body: null, response => response.Lines is not null && response.RecentAudit is not null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> ChangeTenantStatusAsync(string token, Guid id, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/status", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantLineQuotaAsync(string token, Guid id, SetLineQuotaRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/line-quota", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantSandboxModeAsync(string token, Guid id, SetSandboxModeRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/sandbox-mode", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantLineResponse>> DeactivateTenantLineAsync(string token, Guid id, Guid channelId, CancellationToken cancellationToken = default)
        => SendAsync<TenantLineResponse>(HttpMethod.Post, $"api/admin/tenants/{id}/channels/{channelId}/deactivate", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantAiEnabledAsync(string token, Guid id, SetAiEnabledRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/ai-enabled", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantAiScheduleAsync(string token, Guid id, SetAiScheduleRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/ai-schedule", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantAiQuotaAsync(string token, Guid id, SetAiQuotaRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/ai-quota", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetTenantSendRateLimitAsync(string token, Guid id, SetSendRateLimitRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{id}/send-rate-limit", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantMonitoringResponse>> GetAdminTenantMonitoringAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<TenantMonitoringResponse>(HttpMethod.Get, $"api/admin/tenants/{id}/monitoring", token, body: null, response => response.Days is not null, cancellationToken);

    public Task<ApiResult<ReportMonitoringResponse>> GetAdminReportMonitoringAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<ReportMonitoringResponse>(HttpMethod.Get, "api/admin/monitoring/reports", token, body: null, r => r.Totals is not null && r.Companies is not null, cancellationToken);

    public Task<ApiResult<PlatformOverviewResponse>> GetAdminMonitoringAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<PlatformOverviewResponse>(HttpMethod.Get, "api/admin/monitoring", token, body: null, response => response.Flags is not null && response.Health is not null, cancellationToken);

    public Task<ApiResult<PagedResponse<LineLicenseResponse>>> GetAdminLicensesAsync(string token, AdminLicensesQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<LineLicenseResponse>>(HttpMethod.Get, "api/admin/licenses" + AdminQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<LineLicenseResponse>> GetAdminLicenseAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<LineLicenseResponse>(HttpMethod.Get, $"api/admin/licenses/{id}", token, body: null, response => response.Id != Guid.Empty, cancellationToken);

    public Task<ApiResult<LineLicenseResponse>> CreateAdminLicenseAsync(string token, CreateLineLicenseRequest request, CancellationToken cancellationToken = default)
        => SendAsync<LineLicenseResponse>(HttpMethod.Post, "api/admin/licenses", token, request, response => response.Id != Guid.Empty, cancellationToken);

    public Task<ApiResult<LineLicenseResponse>> AssignAdminLicenseAsync(string token, Guid id, AssignLineLicenseRequest request, CancellationToken cancellationToken = default)
        => SendAsync<LineLicenseResponse>(HttpMethod.Post, $"api/admin/licenses/{id}/assign", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<LineLicenseResponse>> ReleaseAdminLicenseAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<LineLicenseResponse>(HttpMethod.Post, $"api/admin/licenses/{id}/release", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<LineLicenseResponse>> RetireAdminLicenseAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<LineLicenseResponse>(HttpMethod.Post, $"api/admin/licenses/{id}/retire", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<PagedResponse<AuditEntryResponse>>> GetAdminAuditAsync(string token, AdminAuditQuery query, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<AuditEntryResponse>>(HttpMethod.Get, "api/admin/audit" + AdminQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<PagedResponse<AuditEntryResponse>>> GetAdminTenantLifecycleAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<AuditEntryResponse>>(HttpMethod.Get, $"api/admin/tenants/{id}/lifecycle?pageSize=50", token, body: null, response => response.Items is not null, cancellationToken);

    private static readonly IReadOnlySet<string> DeletionCodes = new HashSet<string>(StringComparer.Ordinal) { "tenant_not_cancelled" };

    public Task<ApiResult<TenantSummaryResponse>> RequestTenantDataDeletionAsync(string token, Guid id, DeleteTenantDataRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Post, $"api/admin/tenants/{id}/data-deletion", token, request, isUsable: null, cancellationToken, DeletionCodes);

    public Task<ApiResult<ContactSuppressionResponse>> SuppressContactAsync(string token, Guid contactId, CancellationToken cancellationToken = default)
        => SendAsync<ContactSuppressionResponse>(HttpMethod.Post, $"api/contacts/{contactId}/suppression", token, body: null, response => response.ContactId != Guid.Empty, cancellationToken);

    // ---------------------------------------------------------------- billing (docs/BILLING.md)

    /// <summary>The billing refusals the pages word themselves (every code of the API's billing problems).</summary>
    public static readonly IReadOnlySet<string> BillingCodes = new HashSet<string>(
        Enum.GetValues<Conversia.Application.Exceptions.BillingProblem>().Select(Conversia.Application.Exceptions.BillingException.CodeOf), StringComparer.Ordinal);

    public Task<ApiResult<BillingAccountResponse>> GetBillingAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<BillingAccountResponse>(HttpMethod.Get, "api/billing", token, body: null, response => response.Charges is not null, cancellationToken, BillingCodes);

    public Task<ApiResult<PaymentMethodSetupResponse>> GetPaymentMethodSetupAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<PaymentMethodSetupResponse>(HttpMethod.Get, "api/billing/payment-method/setup", token, body: null, response => !string.IsNullOrEmpty(response.PublicKey), cancellationToken, BillingCodes);

    public Task<ApiResult<Unit>> SetPaymentMethodAsync(string token, SetPaymentMethodRequest request, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Put, "api/billing/payment-method", token, request, isUsable: null, cancellationToken, BillingCodes, _sendTimeout);

    public Task<ApiResult<PayNowResponse>> PayChargeNowAsync(string token, Guid chargeId, CancellationToken cancellationToken = default)
        => SendAsync<PayNowResponse>(HttpMethod.Post, $"api/billing/charges/{chargeId}/pay", token, body: null, isUsable: null, cancellationToken, BillingCodes, _sendTimeout);

    public Task<ApiResult<PaymentLinkResponse>> GetPaymentLinkAsync(string token, Guid chargeId, CancellationToken cancellationToken = default)
        => SendAsync<PaymentLinkResponse>(HttpMethod.Post, $"api/billing/charges/{chargeId}/payment-link", token, body: null, response => response.Url.StartsWith("https://", StringComparison.Ordinal), cancellationToken, BillingCodes, _sendTimeout);

    public Task<ApiResult<Unit>> SetBillingCancellationAsync(string token, bool cancel, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, cancel ? "api/billing/cancel" : "api/billing/cancel/undo", token, body: null, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<IReadOnlyList<PlanResponse>>> GetAdminPlansAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<PlanResponse>>(HttpMethod.Get, "api/admin/billing/plans", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<PlanResponse>> CreateAdminPlanAsync(string token, PlanRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PlanResponse>(HttpMethod.Post, "api/admin/billing/plans", token, request, response => response.Id != Guid.Empty, cancellationToken, BillingCodes);

    public Task<ApiResult<PlanResponse>> UpdateAdminPlanAsync(string token, Guid id, PlanRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PlanResponse>(HttpMethod.Put, $"api/admin/billing/plans/{id}", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<AddOnResponse>> CreateAdminAddOnAsync(string token, Guid planId, AddOnRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AddOnResponse>(HttpMethod.Post, $"api/admin/billing/plans/{planId}/add-ons", token, request, response => response.Id != Guid.Empty, cancellationToken, BillingCodes);

    public Task<ApiResult<AdminBillingAccountResponse>> GetAdminBillingAsync(string token, Guid tenantId, CancellationToken cancellationToken = default)
        => SendAsync<AdminBillingAccountResponse>(HttpMethod.Get, $"api/admin/tenants/{tenantId}/billing", token, body: null, response => response.Charges is not null, cancellationToken, BillingCodes);

    public Task<ApiResult<AdminBillingAccountResponse>> StartAdminSubscriptionAsync(string token, Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AdminBillingAccountResponse>(HttpMethod.Post, $"api/admin/tenants/{tenantId}/billing/start", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<AdminBillingAccountResponse>> ChangeAdminSubscriptionAsync(string token, Guid tenantId, ChangeSubscriptionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<AdminBillingAccountResponse>(HttpMethod.Put, $"api/admin/tenants/{tenantId}/billing", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<Unit>> AdminChargeActionAsync(string token, Guid tenantId, Guid chargeId, bool markPaid, ChargeActionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, $"api/admin/tenants/{tenantId}/billing/charges/{chargeId}/{(markPaid ? "mark-paid" : "void")}", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<Unit>> AdminCancelSubscriptionAsync(string token, Guid tenantId, AdminCancelSubscriptionRequest? request, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, request is null ? $"api/admin/tenants/{tenantId}/billing/cancel/undo" : $"api/admin/tenants/{tenantId}/billing/cancel", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<IReadOnlyList<BillingReviewItemResponse>>> GetAdminReviewItemsAsync(string token, Guid? tenantId, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<BillingReviewItemResponse>>(HttpMethod.Get, "api/admin/billing/review-items" + (tenantId is { } id ? $"?tenantId={id}" : string.Empty), token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<Unit>> CloseAdminReviewItemAsync(string token, Guid itemId, ReviewItemRequest request, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, $"api/admin/billing/review-items/{itemId}/review", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<Unit>> ReviewAdminAttemptAsync(string token, Guid tenantId, Guid attemptId, ReviewAttemptRequest request, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Post, $"api/admin/tenants/{tenantId}/billing/attempts/{attemptId}/review", token, request, isUsable: null, cancellationToken, BillingCodes);

    public Task<ApiResult<Application.DTOs.Inbox.InboxResponse>> GetInboxAsync(string token, Application.DTOs.Inbox.InboxQuery query, CancellationToken cancellationToken = default)
        => SendAsync<Application.DTOs.Inbox.InboxResponse>(HttpMethod.Get, "api/inbox" + InboxQueryString.Build(query), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<Application.DTOs.Inbox.ConversationVersionResponse>> GetConversationVersionAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Application.DTOs.Inbox.ConversationVersionResponse>(HttpMethod.Get, $"api/conversations/{id}/version", token, body: null, response => !string.IsNullOrEmpty(response.Version), cancellationToken);

    public Task<ApiResult<Application.DTOs.Templates.ChannelTemplatesResponse>> GetConversationTemplatesAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Application.DTOs.Templates.ChannelTemplatesResponse>(HttpMethod.Get, $"api/conversations/{id}/templates", token, body: null, response => response.Templates is not null, cancellationToken);

    public Task<ApiResult<MessageResponse>> SendTemplateMessageAsync(string token, Guid id, SendTemplateMessageRequest request, CancellationToken cancellationToken = default)
        => SendAsync<MessageResponse>(HttpMethod.Post, $"api/conversations/{id}/template-messages", token, request, isUsable: null, cancellationToken, SendFailure.SendCodes, _sendTimeout);

    public Task<ApiResult<PresenceResponse>> MarkConversationReadAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<PresenceResponse>(HttpMethod.Post, $"api/conversations/{id}/read", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<PresenceResponse>> SendTypingAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<PresenceResponse>(HttpMethod.Post, $"api/conversations/{id}/typing", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<Application.DTOs.Templates.ChannelTemplatesResponse>> GetChannelTemplatesAsync(string token, Guid channelId, CancellationToken cancellationToken = default)
        => SendAsync<Application.DTOs.Templates.ChannelTemplatesResponse>(HttpMethod.Get, $"api/channels/{channelId}/templates", token, body: null, response => response.Templates is not null, cancellationToken);

    public Task<ApiResult<Application.DTOs.Templates.TemplateSyncResponse>> SyncChannelTemplatesAsync(string token, Guid channelId, CancellationToken cancellationToken = default)
        => SendAsync<Application.DTOs.Templates.TemplateSyncResponse>(HttpMethod.Post, $"api/channels/{channelId}/templates/sync", token, body: null, isUsable: null, cancellationToken, SendFailure.TemplateSyncCodes, _sendTimeout);

    public Task<ApiResult<MessagingSettingsResponse>> GetMessagingSettingsAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<MessagingSettingsResponse>(HttpMethod.Get, "api/tenant/messaging-settings", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<MessagingSettingsResponse>> SetMessagingSettingsAsync(string token, SetMessagingSettingsRequest request, CancellationToken cancellationToken = default)
        => SendAsync<MessagingSettingsResponse>(HttpMethod.Put, "api/tenant/messaging-settings", token, request, isUsable: null, cancellationToken);

    public async Task<MediaProxyResponse> OpenMessageMediaAsync(string token, Guid conversationId, Guid messageId, string? range, CancellationToken cancellationToken = default)
    {
        var path = $"api/conversations/{conversationId}/messages/{messageId}/media";
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            // Headers only: the body streams through to the browser. The send timeout bounds the wait for the headers.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if ((_sendTimeout ?? _timeout) is { } allowed)
            {
                limit.CancelAfter(allowed);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            return new MediaProxyResponse((int)response.StatusCode, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportAsync(HttpMethod.Get, path, exception);
            return new MediaProxyResponse(StatusCodes.Status503ServiceUnavailable, null);
        }
    }

    public async Task<ApiResult<MessageResponse>> SendMediaMessageAsync(
        string token, Guid conversationId, Stream content, string? contentType, string? caption, CancellationToken cancellationToken = default)
    {
        var path = $"api/conversations/{conversationId}/media-messages";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if ((_sendTimeout ?? _timeout) is { } allowed)
        {
            limit.CancelAfter(allowed);
        }

        try
        {
            using var file = new StreamContent(content);
            if (!string.IsNullOrWhiteSpace(contentType) && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var type))
            {
                file.Headers.ContentType = type;
            }

            using var form = new MultipartFormDataContent { { file, "file", "adjunto" } };
            if (!string.IsNullOrWhiteSpace(caption))
            {
                form.Add(new StringContent(caption), "caption");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, limit.Token);
            return Carry(await ApiResponseReader.ReadAsync<MessageResponse>(response, JsonOptions, null, limit.Token, SendFailure.MediaSendCodes), HttpMethod.Post, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Carry(ApiResult<MessageResponse>.Unavailable(await ReportAsync(HttpMethod.Post, path, exception)), HttpMethod.Post, path);
        }
    }

    public Task<ApiResult<ChannelResponse>> SetHistoryImportAsync(string token, Guid channelId, SetHistoryImportRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ChannelResponse>(HttpMethod.Put, $"api/channels/{channelId}/history-import", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<TenantSummaryResponse>> SetAdminMediaRetentionAsync(string token, Guid tenantId, SetMediaRetentionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<TenantSummaryResponse>(HttpMethod.Put, $"api/admin/tenants/{tenantId}/media-retention", token, request, isUsable: null, cancellationToken);

    private static bool HasToken(AuthResponse response) => !string.IsNullOrWhiteSpace(response.Token);

    /// <param name="expectedCodes">The refusals this call knows how to word (see <see cref="ApiResponseReader"/>); none by default.</param>
    /// <param name="timeout">The limit of this call when it is not the usual one.</param>
    public Task<ApiResult<IReadOnlyList<ReportDefinitionResponse>>> GetReportDefinitionsAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ReportDefinitionResponse>>(HttpMethod.Get, "api/reports/definitions", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportDefinitionResponse>> GetReportDefinitionAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ReportDefinitionResponse>(HttpMethod.Get, $"api/reports/definitions/{id}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportDefinitionResponse>> CreateReportDefinitionAsync(string token, ReportDefinitionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ReportDefinitionResponse>(HttpMethod.Post, "api/reports/definitions", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportDefinitionResponse>> UpdateReportDefinitionAsync(string token, Guid id, ReportDefinitionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ReportDefinitionResponse>(HttpMethod.Put, $"api/reports/definitions/{id}", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<Unit>> DeleteReportDefinitionAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Delete, $"api/reports/definitions/{id}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportOptionsResponse>> GetReportOptionsAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<ReportOptionsResponse>(HttpMethod.Get, "api/reports/options", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportPreviewResponse>> PreviewReportAsync(string token, Guid definitionId, CancellationToken cancellationToken = default)
        => SendAsync<ReportPreviewResponse>(HttpMethod.Post, $"api/reports/definitions/{definitionId}/preview", token, body: null, r => r.Content is not null, cancellationToken,
            timeout: TimeSpan.FromSeconds(60));

    public Task<ApiResult<PagedResponse<ReportSummaryResponse>>> GetReportsAsync(string token, int page, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<ReportSummaryResponse>>(HttpMethod.Get, $"api/reports?page={Math.Max(page, 1)}&pageSize=20", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportResponse>> GetReportAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ReportResponse>(HttpMethod.Get, $"api/reports/{id}", token, body: null, r => r.Content is not null, cancellationToken);

    public Task<ApiResult<ReportWhatsAppOptInResponse>> GetReportWhatsAppOptInAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<ReportWhatsAppOptInResponse>(HttpMethod.Get, "api/reports/whatsapp-opt-in", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportWhatsAppOptInResponse>> SetReportWhatsAppOptInAsync(string token, ReportWhatsAppOptInRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ReportWhatsAppOptInResponse>(HttpMethod.Put, "api/reports/whatsapp-opt-in", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportWhatsAppOptInResponse>> RemoveReportWhatsAppOptInAsync(string token, CancellationToken cancellationToken = default)
        => SendAsync<ReportWhatsAppOptInResponse>(HttpMethod.Delete, "api/reports/whatsapp-opt-in", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportUnsubscribeResponse>> UnsubscribeFromReportAsync(string unsubscribeToken, CancellationToken cancellationToken = default)
        => SendAsync<ReportUnsubscribeResponse>(HttpMethod.Post, $"api/reports/unsubscribe/{Uri.EscapeDataString(unsubscribeToken)}", token: null, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ReportWhatsAppOptInResponse>> VerifyReportWhatsAppAsync(string token, ReportWhatsAppVerifyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ReportWhatsAppOptInResponse>(HttpMethod.Post, "api/reports/whatsapp-opt-in/verify", token, request, isUsable: null, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, string? token, object? body, Func<T, bool>? isUsable, CancellationToken cancellationToken,
        IReadOnlySet<string>? expectedCodes = null, TimeSpan? timeout = null)
    {
        // The caller's own cancellation still propagates; running out of time does not cancel the caller's token, so it is reported
        // below like any other request that got no answer.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if ((timeout ?? _timeout) is { } allowed)
        {
            limit.CancelAfter(allowed);
        }

        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }

            using var response = await _http.SendAsync(request, limit.Token);
            return Carry(await ApiResponseReader.ReadAsync(response, JsonOptions, isUsable, limit.Token, expectedCodes), method, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Carry(ApiResult<T>.Unavailable(await ReportAsync(method, path, exception)), method, path);
        }
    }

    public async Task<InboxPage> GetInboxPageAsync(string token, Application.DTOs.Inbox.InboxQuery query, string? ifNoneMatch, CancellationToken cancellationToken = default)
    {
        var path = "api/inbox" + InboxQueryString.Build(query);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout is { } allowed)
        {
            limit.CancelAfter(allowed);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(ifNoneMatch))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            }

            using var response = await _http.SendAsync(request, limit.Token);
            var etag = response.Headers.ETag?.ToString();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                return new InboxPage(ApiResult<Application.DTOs.Inbox.InboxResponse>.Success(null!), etag, NotModified: true);
            }

            var result = Carry(await ApiResponseReader.ReadAsync<Application.DTOs.Inbox.InboxResponse>(response, JsonOptions, r => r.Items is not null, limit.Token, null), HttpMethod.Get, path);
            return new InboxPage(result, etag, NotModified: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new InboxPage(Carry(ApiResult<Application.DTOs.Inbox.InboxResponse>.Unavailable(await ReportAsync(HttpMethod.Get, path, exception)), HttpMethod.Get, path), null, false);
        }
    }

    // Hands the code of a failure to the request's state so the shared layout can show it, without any page knowing.
    private ApiResult<T> Carry<T>(ApiResult<T> result, HttpMethod method, string path)
    {
        if (result.ErrorCode is { } code)
        {
            _logger?.LogWarning("API call {Method} {Path} failed with error {ErrorId}", method, PathOf(path), code);
            _errorCodes?.Record(code);
        }

        return result;
    }

    // The code is the user's way to reach support, so nothing here may take it away: a reporter that fails still leaves a code.
    private async Task<string> ReportAsync(HttpMethod method, string path, Exception exception)
    {
        var report = new WebErrorReport(
            KindOf(exception), exception, StatusCode: (exception as UpstreamResponseException) is { } upstream ? (int)upstream.Status : null,
            UpstreamMethod: method.Method, UpstreamPath: PathOf(path));
        try
        {
            if (_errorReporter is not null)
            {
                return await _errorReporter.ReportAsync(report, CancellationToken.None);
            }
        }
        catch (Exception reporterFailure)
        {
            _logger?.LogWarning("The error reporter failed: {ExceptionType}", reporterFailure.GetType().Name);
        }

        var code = ErrorCode.New(DateTime.UtcNow);
        _logger?.LogWarning("API call {Method} {Path} failed: {ExceptionType}; error {ErrorId} was not reported", method, PathOf(path), exception.GetType().Name, code);
        return code;
    }

    private static ErrorKind KindOf(Exception exception) => exception switch
    {
        UpstreamResponseException upstream => upstream.Kind,
        HttpRequestException or OperationCanceledException or IOException or SocketException => ErrorKind.UpstreamUnavailable,
        _ => ErrorKind.UpstreamBadResponse,
    };

    // The query string can hold what the person searched for: it is not part of the report.
    private static string PathOf(string path)
    {
        var query = path.IndexOf('?');
        return query < 0 ? path : path[..query];
    }
}
