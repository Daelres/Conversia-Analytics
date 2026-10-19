using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The organization side of a stand-in API: the branches, advisors and channels the page lists and the writes it makes. The webhook
/// secrets are dummy values the tests look for in the pages, the logs and the cookies.
/// </summary>
internal sealed class OrganizationStub
{
    public static readonly Guid Centro = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    public static readonly Guid Norte = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    public static readonly Guid Closed = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    public static readonly Guid Line = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    public static readonly Guid Retired = Guid.Parse("b0000000-0000-4000-8000-000000000002");

    /// <summary>The key of the webhook address, as a manager's list carries it. The page must never print it in a list.</summary>
    public const string ListedKey = "lk-0123456789abcdefghijklmnopqrstuvwxyz";

    public const string NewKey = "nk-0123456789abcdefghijklmnopqrstuvwxyz";
    public const string NewSecret = "ns-0123456789abcdefghijklmnopqrstuvwxyz";
    public const string RotatedSecret = "rs-0123456789abcdefghijklmnopqrstuvwxyz";

    public IReadOnlyList<BranchResponse> Branches { get; set; } =
    [
        new() { Id = Centro, Name = "Centro", IsActive = true, CreatedAt = new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc) },
        new() { Id = Norte, Name = "Norte", IsActive = true, CreatedAt = new DateTime(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc) },
        new() { Id = Closed, Name = "Sur (cerrada)", IsActive = false, CreatedAt = new DateTime(2026, 9, 3, 15, 0, 0, DateTimeKind.Utc) },
    ];

    public IReadOnlyList<AdvisorResponse> Advisors { get; set; } =
    [
        new() { Id = Guid.Parse("a0000000-0000-4000-8000-000000000001"), Name = "Ana Torres", BranchId = Centro, IsActive = true, CreatedAt = new DateTime(2026, 9, 4, 15, 0, 0, DateTimeKind.Utc) },
        new() { Id = Guid.Parse("a0000000-0000-4000-8000-000000000002"), Name = "Luis Pérez", BranchId = Norte, IsActive = false, CreatedAt = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc) },
    ];

    public IReadOnlyList<ChannelResponse> Channels { get; set; } =
    [
        new() { Id = Line, BranchId = Centro, DisplayPhoneNumber = "+57 300 000 0001", IsActive = true, ChannelKey = ListedKey, WebhookPath = "/api/webhooks/360dialog/" + ListedKey, CreatedAt = new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc) },
        new() { Id = Retired, BranchId = Norte, DisplayPhoneNumber = "+57 300 000 0002", IsActive = false, ChannelKey = ListedKey + "-2", WebhookPath = "/api/webhooks/360dialog/" + ListedKey + "-2", CreatedAt = new DateTime(2026, 9, 7, 15, 0, 0, DateTimeKind.Utc) },
    ];

    /// <summary>The caller's rights; null makes the caller an owner.</summary>
    public IReadOnlyList<Permission>? MemberPermissions { get; set; }

    public Dictionary<string, Func<BackendRequest, HttpResponseMessage>> Overrides { get; } = new();

    public Task<HttpResponseMessage?> Handle(BackendRequest request)
    {
        var path = request.Path.TrimEnd('/');
        var key = $"{request.Method.Method} {path}";
        if (Overrides.TryGetValue(key, out var custom))
        {
            return Task.FromResult<HttpResponseMessage?>(custom(request));
        }

        var canManage = MemberPermissions is null || MemberPermissions.Contains(Permission.OrganizationManage);
        HttpResponseMessage? answer = (request.Method.Method, path) switch
        {
            ("GET", "/api/users/me") when MemberPermissions is not null => FakeBackend.Answer(HttpStatusCode.OK, Member(request)),
            ("GET", "/api/branches") => FakeBackend.Answer(HttpStatusCode.OK, Branches),
            ("GET", "/api/advisors") => FakeBackend.Answer(HttpStatusCode.OK, Advisors),
            ("GET", "/api/channels") => FakeBackend.Answer(HttpStatusCode.OK, canManage ? Channels : Channels.Select(c => c.WithoutWebhookAddress()).ToList()),
            ("GET", _) when ChannelId(path, "") is { } id => One(id, canManage),
            ("POST", "/api/branches") => FakeBackend.Answer(HttpStatusCode.Created, new BranchResponse { Id = Guid.NewGuid(), Name = NameOf(request), IsActive = true }),
            ("POST", "/api/advisors") => FakeBackend.Answer(HttpStatusCode.Created, new AdvisorResponse { Id = Guid.NewGuid(), Name = NameOf(request), IsActive = true }),
            ("POST", "/api/channels") => FakeBackend.Answer(HttpStatusCode.Created, Secret(Guid.NewGuid(), "+57 300 111 2222", NewKey, NewSecret)),
            ("POST", _) when ChannelId(path, "/rotate-secret") is { } id => Channels.Any(c => c.Id == id) ? FakeBackend.Answer(HttpStatusCode.OK, Secret(id, "+57 300 000 0001", ListedKey, RotatedSecret)) : FakeBackend.Raw(HttpStatusCode.NotFound, null),
            ("POST", _) when ChannelId(path, "/activate") is { } id => Toggle(id, true),
            ("POST", _) when ChannelId(path, "/deactivate") is { } id => Toggle(id, false),
            _ => null,
        };
        return Task.FromResult(answer);
    }

    private HttpResponseMessage One(Guid id, bool canManage)
    {
        var channel = Channels.FirstOrDefault(c => c.Id == id);
        return channel is null ? FakeBackend.Raw(HttpStatusCode.NotFound, null) : FakeBackend.Answer(HttpStatusCode.OK, canManage ? channel : channel.WithoutWebhookAddress());
    }

    private HttpResponseMessage Toggle(Guid id, bool active)
    {
        var channel = Channels.FirstOrDefault(c => c.Id == id);
        if (channel is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        channel.IsActive = active;
        return FakeBackend.Answer(HttpStatusCode.OK, channel);
    }

    private static CreateChannelResponse Secret(Guid id, string phone, string key, string secret) => new()
    {
        Id = id, BranchId = Centro, DisplayPhoneNumber = phone, ChannelKey = key, WebhookPath = "/api/webhooks/360dialog/" + key, IsActive = true,
        CreatedAt = DateTime.UtcNow, WebhookSecret = secret,
    };

    private static string NameOf(BackendRequest request)
    {
        using var body = request.BodyJson();
        return body.RootElement.GetProperty("name").GetString()!;
    }

    private static Guid? ChannelId(string path, string suffix)
    {
        const string prefix = "/api/channels/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParse(path[prefix.Length..(path.Length - suffix.Length)], out var id) ? id : null;
    }

    private UserProfileResponse Member(BackendRequest request)
    {
        var (user, tenant) = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]);
        return new UserProfileResponse { UserId = user, TenantId = tenant, IsOwner = false, Permissions = MemberPermissions! };
    }
}

/// <summary>A Web host in front of an <see cref="OrganizationStub"/>.</summary>
internal sealed class OrganizationHarness : IDisposable
{
    private OrganizationHarness(WebTestHost host, FakeBackend backend, OrganizationStub stub)
    {
        Host = host;
        Backend = backend;
        Stub = stub;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public OrganizationStub Stub { get; }

    public static OrganizationHarness Create(IReadOnlyList<Permission>? member = null, Action<OrganizationStub>? configure = null)
    {
        var stub = new OrganizationStub { MemberPermissions = member };
        configure?.Invoke(stub);
        var backend = new FakeBackend { Data = stub.Handle };
        var host = WebTestHost.CreateWithBackend(backend, WebSettings.With(("ErrorReporting:ApiKey", WebScenario.Key)));
        return new OrganizationHarness(host, backend, stub);
    }

    public Task<WebSession> SignInAsync(string email = "owner@example.test") => WebFlows.SignedInAsync(Host, email, WebScenario.Password);

    public async Task<PageResponse> OpenAsync(string url)
    {
        using var session = await SignInAsync();
        return await session.GetAsync(url);
    }

    public IReadOnlyList<BackendRequest> Posts => Backend.DataRequests.Where(r => r.Method == HttpMethod.Post && r.Path.StartsWith("/api/", StringComparison.Ordinal) && r.Path != "/api/auth/login").ToList();

    public void Dispose() => Host.Dispose();
}
