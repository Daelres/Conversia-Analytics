using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Products;
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
    private readonly ILogger<ApiClient>? _logger;
    private readonly IWebErrorReporter? _errorReporter;
    private readonly ErrorCodeState? _errorCodes;

    public ApiClient(HttpClient httpClient, ILogger<ApiClient>? logger = null, IWebErrorReporter? errorReporter = null, ErrorCodeState? errorCodes = null)
    {
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

    public Task<ApiResult<PagedResponse<ProductResponse>>> GetProductsAsync(string token, int page, int pageSize, string? search, CancellationToken cancellationToken = default)
        => SendAsync<PagedResponse<ProductResponse>>(HttpMethod.Get, ProductListPath(page, pageSize, search), token, body: null, response => response.Items is not null, cancellationToken);

    public Task<ApiResult<ProductResponse>> GetProductAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ProductResponse>(HttpMethod.Get, $"api/products/{id}", token, body: null, isUsable: null, cancellationToken);

    public Task<ApiResult<ProductResponse>> CreateProductAsync(string token, CreateProductRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ProductResponse>(HttpMethod.Post, "api/products", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<ProductResponse>> UpdateProductAsync(string token, Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ProductResponse>(HttpMethod.Put, $"api/products/{id}", token, request, isUsable: null, cancellationToken);

    public Task<ApiResult<Unit>> DeleteProductAsync(string token, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Unit>(HttpMethod.Delete, $"api/products/{id}", token, body: null, isUsable: null, cancellationToken);

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

    private static string ProductListPath(int page, int pageSize, string? search)
    {
        var path = $"api/products?page={page}&pageSize={pageSize}";
        return string.IsNullOrWhiteSpace(search) ? path : $"{path}&search={Uri.EscapeDataString(search)}";
    }

    private static bool HasToken(AuthResponse response) => !string.IsNullOrWhiteSpace(response.Token);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, string? token, object? body, Func<T, bool>? isUsable, CancellationToken cancellationToken)
    {
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

            using var response = await _http.SendAsync(request, cancellationToken);
            return Carry(await ApiResponseReader.ReadAsync(response, JsonOptions, isUsable, cancellationToken), method, path);
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
