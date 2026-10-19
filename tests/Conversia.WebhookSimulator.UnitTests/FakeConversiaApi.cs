using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Products;
using Conversia.Application.Ingestion;
using Conversia.WebhookSimulator.Api;

namespace Conversia.WebhookSimulator.UnitTests;

/// <summary>
/// An in-memory Conversia API as the simulator sees it: login, branches, advisors, channels (their secrets are shown once), the webhook
/// (which reads the body with the product's own parser) and the conversation list with the advisor assignment. No sockets: it is the HttpMessageHandler.
/// </summary>
internal sealed class FakeConversiaApi : HttpMessageHandler
{
    public const string Email = "owner@example.test";
    public const string Password = "pw-dummy-for-tests";

    private readonly Dictionary<Guid, string> _secretHashes = [];
    private readonly Dictionary<Guid, string> _keys = [];
    private readonly HashSet<string> _seenMessages = [];
    private readonly Dictionary<(Guid Channel, string WaId), ConversationListItem> _conversations = [];
    private int _secretCounter;
    private string? _token;

    public List<BranchResponse> Branches { get; } = [];

    public List<AdvisorResponse> Advisors { get; } = [];

    public List<ChannelResponse> Channels { get; } = [];

    /// <summary>Every request, in order: method, path and query (the secret header is not kept).</summary>
    public List<string> Calls { get; } = [];

    public List<(string Key, string Body)> Deliveries { get; } = [];

    public List<(string Path, string Body)> Writes { get; } = [];

    /// <summary>The secret each channel was last given, by display number; a test reads it to play the part of a person who copied it.</summary>
    public Dictionary<Guid, string> IssuedSecrets { get; } = [];

    /// <summary>Answers the next conversation list requests with a 429 this many times first.</summary>
    public int ThrottleListings { get; set; }

    /// <summary>Answers every advisor assignment with a 400, as the API does for an advisor the conversation cannot have.</summary>
    public bool RefuseAssignments { get; set; }

    public IReadOnlyCollection<ConversationListItem> Conversations => _conversations.Values;

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://localhost:5030/") };

    public string SecretOf(string branchName)
    {
        var branch = Branches.Single(b => b.Name == branchName);
        var channel = Channels.First(c => c.BranchId == branch.Id);
        return IssuedSecrets[channel.Id];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        Calls.Add($"{request.Method} /{path}{request.RequestUri.Query}");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        if (path == "health")
        {
            return Text(HttpStatusCode.OK, "Healthy");
        }

        if (path.StartsWith("api/webhooks/360dialog/", StringComparison.Ordinal))
        {
            return Webhook(path["api/webhooks/360dialog/".Length..], request.Headers.TryGetValues("X-Webhook-Secret", out var secret) ? secret.Single() : null, body!);
        }

        if (path == "api/auth/login")
        {
            var login = JsonSerializer.Deserialize<LoginRequest>(body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (login.Email != Email || login.Password != Password)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            _token = "token-" + Guid.NewGuid().ToString("N");
            return Json(HttpStatusCode.OK, new AuthResponse { Token = _token, IsOwner = true });
        }

        if (request.Headers.Authorization?.Parameter != _token || _token is null)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var segments = path.Split('/');
        return (request.Method.Method, segments) switch
        {
            ("GET", ["api", "branches"]) => Json(HttpStatusCode.OK, Branches),
            ("POST", ["api", "branches"]) => CreateBranch(Read<CreateBranchRequest>(body!)),
            ("GET", ["api", "advisors"]) => Json(HttpStatusCode.OK, Advisors),
            ("POST", ["api", "advisors"]) => CreateAdvisor(Read<CreateAdvisorRequest>(body!)),
            ("GET", ["api", "channels"]) => Json(HttpStatusCode.OK, Channels),
            ("POST", ["api", "channels"]) => CreateChannel(Read<CreateChannelRequest>(body!)),
            ("POST", ["api", "channels", var id, "rotate-secret"]) => Rotate(Guid.Parse(id)),
            ("GET", ["api", "conversations"]) => ListConversations(request.RequestUri.Query),
            ("PUT", ["api", "conversations", var id, "advisor"]) => AssignAdvisor(Guid.Parse(id), Read<AssignAdvisorRequest>(body!), path, body!),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage CreateBranch(CreateBranchRequest request)
    {
        var branch = new BranchResponse { Id = Guid.NewGuid(), Name = request.Name, IsActive = true, CreatedAt = DateTime.UtcNow };
        Branches.Add(branch);
        return Json(HttpStatusCode.Created, branch);
    }

    private HttpResponseMessage CreateAdvisor(CreateAdvisorRequest request)
    {
        var advisor = new AdvisorResponse { Id = Guid.NewGuid(), Name = request.Name, BranchId = request.BranchId!.Value, IsActive = true, CreatedAt = DateTime.UtcNow };
        Advisors.Add(advisor);
        return Json(HttpStatusCode.Created, advisor);
    }

    private HttpResponseMessage CreateChannel(CreateChannelRequest request)
    {
        var channel = new ChannelResponse
        {
            Id = Guid.NewGuid(), BranchId = request.BranchId!.Value, DisplayPhoneNumber = request.DisplayPhoneNumber, IsActive = true, CreatedAt = DateTime.UtcNow,
            ChannelKey = "key-" + Guid.NewGuid().ToString("N"),
        };
        channel.WebhookPath = WebhookRoutes.Dialog360Path(channel.ChannelKey);
        Channels.Add(channel);
        _keys[channel.Id] = channel.ChannelKey;
        return Json(HttpStatusCode.Created, WithSecret(channel));
    }

    private HttpResponseMessage Rotate(Guid id)
    {
        var channel = Channels.Single(c => c.Id == id);
        return Json(HttpStatusCode.OK, WithSecret(channel));
    }

    private CreateChannelResponse WithSecret(ChannelResponse channel)
    {
        var secret = $"secret-{++_secretCounter}-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        _secretHashes[channel.Id] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
        IssuedSecrets[channel.Id] = secret;
        return new CreateChannelResponse
        {
            Id = channel.Id, BranchId = channel.BranchId, DisplayPhoneNumber = channel.DisplayPhoneNumber, ChannelKey = channel.ChannelKey, WebhookPath = channel.WebhookPath,
            IsActive = channel.IsActive, CreatedAt = channel.CreatedAt, WebhookSecret = secret,
        };
    }

    private HttpResponseMessage Webhook(string key, string? secret, string body)
    {
        var channel = Channels.FirstOrDefault(c => c.ChannelKey == key && c.IsActive);
        if (channel is null || secret is null || !_secretHashes.TryGetValue(channel.Id, out var hash)
            || hash != Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        Deliveries.Add((key, body));
        var branch = Branches.Single(b => b.Id == channel.BranchId);
        foreach (var message in Dialog360PayloadParser.Parse(body).Messages)
        {
            if (!_seenMessages.Add(channel.Id + "/" + message.ProviderMessageId))
            {
                continue;
            }

            if (!_conversations.TryGetValue((channel.Id, message.ContactWaId), out var item))
            {
                item = new ConversationListItem
                {
                    Id = Guid.NewGuid(), Contact = new ContactSummary { Id = Guid.NewGuid(), WaId = message.ContactWaId, DisplayName = message.ContactName },
                    ChannelId = channel.Id, BranchId = branch.Id, BranchName = branch.Name, StartedAt = message.SentAt, Status = "Open", Source = message.Referral is null ? "Organic" : "Ads",
                };
                _conversations[(channel.Id, message.ContactWaId)] = item;
            }

            item.MessageCount++;
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private HttpResponseMessage ListConversations(string query)
    {
        if (ThrottleListings > 0)
        {
            ThrottleListings--;
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return throttled;
        }

        var values = System.Web.HttpUtility.ParseQueryString(query);
        var page = int.Parse(values["page"] ?? "1");
        var size = int.Parse(values["pageSize"] ?? "20");
        var all = _conversations.Values.OrderBy(c => c.StartedAt).ToList();
        return Json(HttpStatusCode.OK, new PagedResponse<ConversationListItem> { Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = all.Count });
    }

    private HttpResponseMessage AssignAdvisor(Guid conversationId, AssignAdvisorRequest request, string path, string body)
    {
        Writes.Add((path, body));
        var item = _conversations.Values.Single(c => c.Id == conversationId);
        var advisor = Advisors.Single(a => a.Id == request.AdvisorId);
        if (RefuseAssignments || advisor.BranchId != item.BranchId)
        {
            return Text(HttpStatusCode.BadRequest, "advisor of another branch");
        }

        item.AdvisorId = advisor.Id;
        item.AdvisorName = advisor.Name;
        return Json(HttpStatusCode.OK, item);
    }

    private static T Read<T>(string body) => JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    private static HttpResponseMessage Text(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text) };
}
