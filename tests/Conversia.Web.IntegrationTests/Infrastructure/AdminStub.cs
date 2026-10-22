using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The administrator side of a stand-in API: a few companies, the audit trail and the writes the pages make, with no database. It is for the
/// states the real API cannot be put in on demand (a company that has a last activity, a 409 of a given wording, a 404, an expired token);
/// the flows with real rules run against the real API (<see cref="ApiSeed"/>).
/// </summary>
internal sealed class AdminStub
{
    public const string Password = "Stub-Adm1n-Passw0rd!";
    public const string Email = "admin@stub.test";

    public static readonly Guid AdminId = Guid.Parse("ad000000-0000-4000-8000-000000000001");
    public static readonly Guid Aurora = Guid.Parse("d0000000-0000-4000-8000-000000000001");
    public static readonly Guid Norte = Guid.Parse("d0000000-0000-4000-8000-000000000002");
    public static readonly Guid Sur = Guid.Parse("d0000000-0000-4000-8000-000000000003");
    public static readonly Guid AuroraLine = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    public static readonly Guid AuroraOffLine = Guid.Parse("e0000000-0000-4000-8000-000000000002");

    public static readonly DateTime LastActivity = new(2026, 10, 20, 15, 5, 0, DateTimeKind.Utc);

    public List<TenantDetailResponse> Companies { get; } =
    [
        new()
        {
            Id = Aurora, Name = "Depilación Aurora", Status = TenantStatus.Active, MaxActiveLines = 2, ActiveLines = 1, RegisteredLines = 2,
            OwnerEmail = "aurora@stub.test", RegisteredAt = new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc), LastActivityAt = LastActivity,
            StatusChangedAt = new DateTime(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc), StatusReason = "Acuerdo firmado",
            Lines =
            [
                new() { ChannelId = AuroraLine, DisplayPhoneNumber = "+57 300 000 0001", BranchName = "Centro", IsActive = true },
                new() { ChannelId = AuroraOffLine, DisplayPhoneNumber = "+57 300 000 0002", BranchName = null, IsActive = false },
            ],
        },
        new()
        {
            Id = Norte, Name = "Estética Norte", Status = TenantStatus.Pending, MaxActiveLines = 0, ActiveLines = 0, RegisteredLines = 0,
            OwnerEmail = "norte@stub.test", RegisteredAt = new DateTime(2026, 10, 18, 15, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            Id = Sur, Name = "Clínica <b>Sur</b>", Status = TenantStatus.Suspended, MaxActiveLines = 4, ActiveLines = 0, RegisteredLines = 1,
            OwnerEmail = null, RegisteredAt = new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc),
            StatusChangedAt = new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc), StatusReason = "<script>alert(1)</script>",
        },
    ];

    public List<AuditEntryResponse> Audit { get; } = [];

    /// <summary>Answers a call before the stub does: key "METHOD /path".</summary>
    public Dictionary<string, Func<BackendRequest, HttpResponseMessage>> Overrides { get; } = new();

    public Task<HttpResponseMessage?> Handle(BackendRequest request)
    {
        var path = request.Path.TrimEnd('/');
        if (Overrides.TryGetValue($"{request.Method.Method} {path}", out var custom))
        {
            return Task.FromResult<HttpResponseMessage?>(custom(request));
        }

        HttpResponseMessage? answer = (request.Method.Method, path) switch
        {
            ("POST", "/api/admin/auth/login") => Login(request),
            ("GET", "/api/admin/tenants") => List(request),
            ("GET", "/api/admin/audit") => FakeBackend.Answer(HttpStatusCode.OK, Page(Audit, 1, 20)),
            ("GET", _) when TenantId(path, "") is { } id => Detail(id),
            ("PUT", _) when TenantId(path, "/status") is { } id => ChangeStatus(id, request),
            ("PUT", _) when TenantId(path, "/line-quota") is { } id => SetQuota(id, request),
            _ => null,
        };
        return Task.FromResult(answer);
    }

    private static HttpResponseMessage Login(BackendRequest request)
    {
        using var body = request.BodyJson();
        var ok = body.RootElement.GetProperty("email").GetString() == Email && body.RootElement.GetProperty("password").GetString() == Password;
        return ok
            ? FakeBackend.Answer(HttpStatusCode.OK, new AdminAuthResponse { Token = FakeJwt.Create(AdminId, Guid.Empty), AdminId = AdminId, ExpiresAt = DateTime.UtcNow.AddHours(1) })
            : FakeBackend.Raw(HttpStatusCode.Unauthorized, null);
    }

    private HttpResponseMessage List(BackendRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        IEnumerable<TenantDetailResponse> companies = Companies;
        if (Enum.TryParse<TenantStatus>(query["status"], out var status))
        {
            companies = companies.Where(c => c.Status == status);
        }

        if (query["search"] is { Length: > 0 } search)
        {
            companies = companies.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var page = int.TryParse(query["page"], out var p) ? p : 1;
        var size = int.TryParse(query["pageSize"], out var s) ? s : 20;
        return FakeBackend.Answer(HttpStatusCode.OK, Page(companies.Select(Summary).ToList(), page, size));
    }

    private HttpResponseMessage Detail(Guid id)
        => Companies.FirstOrDefault(c => c.Id == id) is { } company
            ? FakeBackend.Answer(HttpStatusCode.OK, company)
            : FakeBackend.Raw(HttpStatusCode.NotFound, """{"title":"Empresa no encontrada","status":404,"detail":"Empresa no encontrada."}""");

    private HttpResponseMessage ChangeStatus(Guid id, BackendRequest request)
    {
        var company = Companies.FirstOrDefault(c => c.Id == id);
        if (company is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        using var body = request.BodyJson();
        var target = Enum.Parse<TenantStatus>(body.RootElement.GetProperty("status").GetString()!);
        company.Status = target;
        return FakeBackend.Answer(HttpStatusCode.OK, Summary(company));
    }

    private HttpResponseMessage SetQuota(Guid id, BackendRequest request)
    {
        var company = Companies.FirstOrDefault(c => c.Id == id);
        if (company is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        using var body = request.BodyJson();
        company.MaxActiveLines = body.RootElement.GetProperty("maxActiveLines").GetInt32();
        return FakeBackend.Answer(HttpStatusCode.OK, Summary(company));
    }

    private static TenantSummaryResponse Summary(TenantDetailResponse company) => new()
    {
        Id = company.Id, Name = company.Name, Status = company.Status, MaxActiveLines = company.MaxActiveLines, ActiveLines = company.ActiveLines,
        RegisteredLines = company.RegisteredLines, OwnerEmail = company.OwnerEmail, RegisteredAt = company.RegisteredAt,
        LastActivityAt = company.LastActivityAt, StatusChangedAt = company.StatusChangedAt, StatusReason = company.StatusReason,
    };

    private static PagedResponse<T> Page<T>(IReadOnlyList<T> all, int page, int size) => new()
    {
        Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = all.Count,
    };

    private static Guid? TenantId(string path, string suffix)
    {
        const string prefix = "/api/admin/tenants/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParse(path[prefix.Length..(path.Length - suffix.Length)], out var id) ? id : null;
    }

    public static AuditEntryResponse Entry(string action, Guid tenantId, string json, DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(), OccurredAt = at ?? new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), AdminId = AdminId, AdminEmail = Email,
        Action = action, TenantId = tenantId, Details = JsonDocument.Parse(json).RootElement.Clone(),
    };
}

/// <summary>A Web host in front of an <see cref="AdminStub"/>.</summary>
internal sealed class AdminHarness : IDisposable
{
    private AdminHarness(WebTestHost host, FakeBackend backend, AdminStub stub)
    {
        Host = host;
        Backend = backend;
        Stub = stub;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public AdminStub Stub { get; }

    public static AdminHarness Create(Action<AdminStub>? configure = null)
    {
        var stub = new AdminStub();
        configure?.Invoke(stub);
        var backend = new FakeBackend { Data = stub.Handle };
        var host = WebTestHost.CreateWithBackend(backend, WebSettings.With(("ErrorReporting:ApiKey", WebScenario.Key)));
        return new AdminHarness(host, backend, stub);
    }

    public async Task<WebSession> SignInAsync()
    {
        var session = Host.NewSession();
        var response = await WebFlows.AdminLoginAsync(session, AdminStub.Email, AdminStub.Password);
        Assert.True(response.IsRedirect, $"Admin login should redirect, got {response.Status}. Page text: {response.Text}");
        return session;
    }

    public async Task<PageResponse> OpenAsync(string url)
    {
        using var session = await SignInAsync();
        return await session.GetAsync(url);
    }

    /// <summary>Writes the Web sent to the stand-in administrator API.</summary>
    public IReadOnlyList<BackendRequest> Writes => Backend.DataRequests
        .Where(r => r.Path.StartsWith("/api/admin/", StringComparison.Ordinal) && r.Method != HttpMethod.Get && r.Path != "/api/admin/auth/login").ToList();

    public void Dispose() => Host.Dispose();
}
