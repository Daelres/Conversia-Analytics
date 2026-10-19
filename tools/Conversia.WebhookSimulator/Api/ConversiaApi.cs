using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Products;

namespace Conversia.WebhookSimulator.Api;

/// <summary>The few calls of the Conversia API the simulator needs, made with the token of the person who runs it.</summary>
public sealed class ConversiaApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ConversiaApi(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new LoginRequest { Email = email, Password = password }, Json, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            throw new ApiFailure("The API refused the login: check SIM_EMAIL and SIM_PASSWORD (and that the workspace exists).", response.StatusCode);
        }

        var auth = await ReadAsync<AuthResponse>(response, "login", cancellationToken);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
    }

    /// <summary>The API answers <c>200</c> with a status word (<c>Healthy</c> or <c>Degraded</c>); <c>503</c> means the queue cannot be read.</summary>
    public async Task<(bool Ok, string Status)> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("health", cancellationToken);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return (response.IsSuccessStatusCode, body.Length is > 0 and <= 20 ? body : response.StatusCode.ToString());
        }
        catch (HttpRequestException)
        {
            return (false, "unreachable");
        }
    }

    public Task<IReadOnlyList<BranchResponse>> GetBranchesAsync(CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<BranchResponse>>("api/branches", cancellationToken);

    public Task<BranchResponse> CreateBranchAsync(string name, CancellationToken cancellationToken)
        => SendAsync<BranchResponse>(HttpMethod.Post, "api/branches", new CreateBranchRequest { Name = name }, cancellationToken);

    public Task<IReadOnlyList<AdvisorResponse>> GetAdvisorsAsync(CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<AdvisorResponse>>("api/advisors", cancellationToken);

    public Task<AdvisorResponse> CreateAdvisorAsync(string name, Guid branchId, CancellationToken cancellationToken)
        => SendAsync<AdvisorResponse>(HttpMethod.Post, "api/advisors", new CreateAdvisorRequest { Name = name, BranchId = branchId }, cancellationToken);

    public Task<IReadOnlyList<ChannelResponse>> GetChannelsAsync(CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<ChannelResponse>>("api/channels", cancellationToken);

    /// <summary>The answer carries the webhook secret, the only time the API ever gives it.</summary>
    public Task<CreateChannelResponse> CreateChannelAsync(Guid branchId, string displayPhoneNumber, CancellationToken cancellationToken)
        => SendAsync<CreateChannelResponse>(HttpMethod.Post, "api/channels", new CreateChannelRequest { BranchId = branchId, DisplayPhoneNumber = displayPhoneNumber }, cancellationToken);

    public Task<CreateChannelResponse> RotateSecretAsync(Guid channelId, CancellationToken cancellationToken)
        => SendAsync<CreateChannelResponse>(HttpMethod.Post, $"api/channels/{channelId}/rotate-secret", body: null, cancellationToken);

    public async Task<int> CountConversationsAsync(CancellationToken cancellationToken)
        => (await GetAsync<PagedResponse<ConversationListItem>>("api/conversations?page=1&pageSize=1", cancellationToken)).TotalCount;

    /// <summary>Every conversation, page by page (the list is throttled, so each page waits out a 429).</summary>
    public async Task<IReadOnlyList<ConversationListItem>> ListAllConversationsAsync(CancellationToken cancellationToken)
    {
        var all = new List<ConversationListItem>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<PagedResponse<ConversationListItem>>($"api/conversations?page={page}&pageSize={ConversationQuery.MaxPageSize}", cancellationToken);
            all.AddRange(result.Items);
            if (all.Count >= result.TotalCount || result.Items.Count == 0)
            {
                return all;
            }
        }
    }

    public Task AssignAdvisorAsync(Guid conversationId, Guid advisorId, CancellationToken cancellationToken)
        => SendAsync<ConversationListItem>(HttpMethod.Put, $"api/conversations/{conversationId}/advisor", new AssignAdvisorRequest { AdvisorId = advisorId }, cancellationToken);

    private Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) => SendAsync<T>(HttpMethod.Get, path, body: null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt <= 6)
            {
                await _delay(RetryAfter(response), cancellationToken);
                continue;
            }

            return await ReadAsync<T>(response, $"{method} {path.Split('?')[0]}", cancellationToken);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string call, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " (the token was refused)",
                HttpStatusCode.Forbidden => " (this user lacks the permission: the simulator needs an owner or Organization and Conversations access)",
                HttpStatusCode.TooManyRequests => " (rate limit: try again in a minute)",
                _ => string.Empty,
            };
            throw new ApiFailure($"{call} answered {(int)response.StatusCode} {response.StatusCode}{hint}.", response.StatusCode);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        return value ?? throw new ApiFailure($"{call} answered nothing readable.", response.StatusCode);
    }

    /// <summary>How long the API asked to wait (seconds), at least one second and at most a minute.</summary>
    public static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }
}
