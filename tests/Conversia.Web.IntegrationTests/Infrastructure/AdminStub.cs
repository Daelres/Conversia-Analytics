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
    public static readonly Guid AuroraLicense = Guid.Parse("f0000000-0000-4000-8000-000000000001");
    public static readonly Guid FreeLicense = Guid.Parse("f0000000-0000-4000-8000-000000000002");

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
            Limits = new()
            {
                MaxActiveLines = 2, ActiveLines = 1, AiEnabled = false, AiMonthlyConversationQuota = 500, AiQuotaIsDefault = true, DefaultAiMonthlyConversationQuota = 500,
                AiMonthlyUsdCeiling = 10m, SendRateLimitPerMinute = 45, SendRateLimitIsDefault = false, DefaultSendRateLimitPerMinute = 120, MaxSendRateLimitPerMinute = 1200,
                AiAnalysisMode = "Nightly", AiAnalysisNightlyHour = 1, AiAnalysisNightlyHourIsDefault = true, NextAiAnalysisWindowAt = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc),
            },
            Licenses =
            [
                new() { Id = AuroraLicense, Provider = "360dialog", PhoneNumberMasked = "•••• 0001", Status = "Assigned", AssignedTenantId = Aurora, AssignedTenantName = "Depilación Aurora", ChannelId = AuroraLine, MonthlyCostUsd = 15m },
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

    public List<LineLicenseResponse> Licenses { get; } =
    [
        new() { Id = AuroraLicense, Provider = "360dialog", PhoneNumberMasked = "•••• 0001", Status = "Assigned", AssignedTenantId = Aurora, AssignedTenantName = "Depilación Aurora", ChannelId = AuroraLine, MonthlyCostUsd = 15m },
        new() { Id = FreeLicense, Provider = "360dialog", PhoneNumberMasked = "•••• 9876", Status = "Available", MonthlyCostUsd = 12.5m, Notes = "Lote <b>noviembre</b>" },
    ];

    public TenantMonitoringResponse Monitoring { get; set; } = new()
    {
        TenantId = Aurora, ActiveLines = 1, MaxActiveLines = 2, AssignedLicenses = 1, LastRealDeliveryAt = LastActivity,
        Deliveries = new() { RealLast24h = new() { Events = 7, StoredMessages = 9, DuplicateMessages = 1, SkippedMessages = 0, FailedEvents = 1 } },
        Ai = new() { Enabled = false, State = "Disabled", Quota = 500, QuotaIsDefault = true, CeilingUsd = 10m, ThisMonth = new() { Conversations = 0 }, SandboxThisMonth = new() { Conversations = 3, EstimatedUsd = 0.01m } },
        Sends = new() { RealLast7d = 4, RealFailedLast7d = 1 },
        AiAnalysis = new()
        {
            Mode = "Nightly", NightlyHour = 1, NextWindowAt = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc), Pending = 12,
            LastRun = new()
            {
                WindowStart = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc), WindowEnd = new DateTime(2026, 11, 12, 11, 0, 0, DateTimeKind.Utc), Mode = "Nightly",
                StartedAt = new DateTime(2026, 11, 12, 6, 7, 0, DateTimeKind.Utc), LastActivityAt = new DateTime(2026, 11, 12, 6, 41, 0, DateTimeKind.Utc),
                Claimed = 40, Analysed = 31, Unchanged = 4, Skipped = 2, Discarded = 1, Failed = 1, Deferred = 1, Batched = 33,
            },
        },
        Days =
        [
            new() { Day = "2026-10-20", IsSandbox = false, MessagesIn = 5, MessagesOut = 4, ConversationsOpened = 2, ConversationsClosed = 1 },
            new() { Day = "2026-10-20", IsSandbox = true, MessagesIn = 2, MessagesOut = 0, ConversationsOpened = 1, ConversationsClosed = 0 },
        ],
    };

    public PlatformOverviewResponse Overview { get; set; } = new()
    {
        GeneratedAt = LastActivity,
        Health = new() { Status = "Degraded", Checks = [new() { Name = "webhook-queue", Status = "Degraded" }] },
        Backlog = new() { WebhookPending = 612, OldestPendingSeconds = 900, ClassificationWaiting = 3, OldestClassificationSeconds = 30, ClassificationStopped = 0 },
        Ai = new()
        {
            Provider = "Llm", BreakerState = "Open", Real = new() { Conversations = 40, Calls = 45 }, Sandbox = new() { Calls = 2 }, DeploymentUsd = 3.5m, DeploymentCapUsd = 20m, LostUsageRecords = 2,
            Batches = new() { Submitted = 1, Ended = 0, FailedLast24h = 1, CollectedLast24h = 3, OldestSubmittedAt = new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc) },
        },
        Errors = new() { Last24h = 2, TopLast24h = [new() { Fingerprint = "abc123", Source = "Api", Kind = "Unhandled", Count = 2, LastOccurredAt = LastActivity }] },
        Sends = new() { RealLast7d = 10, RealFailedLast7d = 1, ByOutcomeLast7d = [new() { Outcome = "sent", Real = 9, Sandbox = 1 }, new() { Outcome = "provider_limit_reached", Real = 1 }] },
        Flags = [new() { Code = "webhook_backlog", Severity = "warning", Message = "Hay 612 entregas de webhook pendientes (umbral 500)." }, new() { Code = "health", Severity = "critical", Message = "La comprobación de salud falla." }],
        SilentCompaniesTotal = 1,
        SilentCompanies = [new() { TenantId = Aurora, Name = "Depilación Aurora", ActiveLines = 1, LastDeliveryAt = null }],
        Thresholds = new() { SilentLineHours = 24 },
    };

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
            ("GET", "/api/admin/monitoring") => FakeBackend.Answer(HttpStatusCode.OK, Overview),
            ("GET", _) when TenantId(path, "/monitoring") is { } id => Companies.Any(c => c.Id == id) ? FakeBackend.Answer(HttpStatusCode.OK, Monitoring) : FakeBackend.Raw(HttpStatusCode.NotFound, null),
            ("PUT", _) when TenantId(path, "/ai-enabled") is { } id => SetAi(id, request),
            ("PUT", _) when TenantId(path, "/ai-quota") is { } id => FakeBackend.Answer(HttpStatusCode.OK, Summary(Companies.First(c => c.Id == id))),
            ("PUT", _) when TenantId(path, "/ai-schedule") is { } id => FakeBackend.Answer(HttpStatusCode.OK, Summary(Companies.First(c => c.Id == id))),
            ("PUT", _) when TenantId(path, "/send-rate-limit") is { } id => FakeBackend.Answer(HttpStatusCode.OK, Summary(Companies.First(c => c.Id == id))),
            ("PUT", _) when TenantId(path, "/media-retention") is { } id => FakeBackend.Answer(HttpStatusCode.OK, Summary(Companies.First(c => c.Id == id))),
            ("GET", "/api/admin/licenses") => FakeBackend.Answer(HttpStatusCode.OK, Page(Licenses, 1, 20)),
            ("POST", "/api/admin/licenses") => CreateLicense(request),
            ("GET", _) when LicenseId(path, "") is { } id => Licenses.FirstOrDefault(l => l.Id == id) is { } l
                ? FakeBackend.Answer(HttpStatusCode.OK, l)
                : FakeBackend.Raw(HttpStatusCode.NotFound, """{"title":"Licencia no encontrada","status":404,"detail":"Licencia no encontrada."}"""),
            ("POST", _) when LicenseId(path, "/assign") is { } id => MoveLicense(id, "Assigned", request),
            ("POST", _) when LicenseId(path, "/release") is { } id => MoveLicense(id, "Available", request),
            ("POST", _) when LicenseId(path, "/retire") is { } id => MoveLicense(id, "Retired", request),
            ("GET", _) when TenantId(path, "/lifecycle") is { } id => FakeBackend.Answer(HttpStatusCode.OK, Page(Audit.Where(a => a.TenantId == id).ToList(), 1, 50)),
            ("POST", _) when TenantId(path, "/data-deletion") is { } id => DeleteData(id, request),
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

    /// <summary>The bodies the data-deletion endpoint received, in order.</summary>
    public List<string> DeletionRequests { get; } = [];

    private HttpResponseMessage DeleteData(Guid id, BackendRequest request)
    {
        var company = Companies.FirstOrDefault(c => c.Id == id);
        if (company is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        using var body = request.BodyJson();
        DeletionRequests.Add(body.RootElement.GetRawText());
        if (company.Status != TenantStatus.Cancelled)
        {
            return FakeBackend.Raw(HttpStatusCode.Conflict, """{"title":"Cambio de estado no permitido","status":409,"detail":"Solo se pueden eliminar los datos de una empresa cancelada. Cancélala primero.","code":"tenant_not_cancelled"}""");
        }

        if (!string.Equals(body.RootElement.GetProperty("confirmName").GetString(), company.Name, StringComparison.OrdinalIgnoreCase))
        {
            return FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"Solicitud no válida","status":400,"detail":"El nombre escrito no coincide con el nombre de la empresa. Escríbelo tal como aparece."}""");
        }

        company.DataDeletionStartedAt = DateTime.UtcNow;
        return FakeBackend.Answer(HttpStatusCode.Accepted, Summary(company));
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
        LastActivityAt = company.LastActivityAt, StatusChangedAt = company.StatusChangedAt, StatusReason = company.StatusReason, AiEnabled = company.AiEnabled,
        SuspensionCause = company.SuspensionCause, CancelledAt = company.CancelledAt, DataDeletionDueAt = company.DataDeletionDueAt,
        DataDeletionStartedAt = company.DataDeletionStartedAt,
    };

    private static PagedResponse<T> Page<T>(IReadOnlyList<T> all, int page, int size) => new()
    {
        Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = all.Count,
    };

    private HttpResponseMessage SetAi(Guid id, BackendRequest request)
    {
        var company = Companies.First(c => c.Id == id);
        using var body = request.BodyJson();
        company.AiEnabled = body.RootElement.GetProperty("enabled").GetBoolean();
        company.Limits.AiEnabled = company.AiEnabled;
        return FakeBackend.Answer(HttpStatusCode.OK, Summary(company));
    }

    private HttpResponseMessage CreateLicense(BackendRequest request)
    {
        using var body = request.BodyJson();
        var digits = new string([.. body.RootElement.GetProperty("phoneNumber").GetString()!.Where(char.IsAsciiDigit)]);
        var created = new LineLicenseResponse { Id = Guid.NewGuid(), Provider = "360dialog", PhoneNumberMasked = "•••• " + digits[^4..], Status = "Available", MonthlyCostUsd = body.RootElement.GetProperty("monthlyCostUsd").GetDecimal() };
        Licenses.Add(created);
        return FakeBackend.Answer(HttpStatusCode.Created, created);
    }

    private HttpResponseMessage MoveLicense(Guid id, string status, BackendRequest request)
    {
        var license = Licenses.FirstOrDefault(l => l.Id == id);
        if (license is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        license.Status = status;
        if (status == "Assigned")
        {
            using var body = request.BodyJson();
            license.AssignedTenantId = body.RootElement.GetProperty("tenantId").GetGuid();
        }

        return FakeBackend.Answer(HttpStatusCode.OK, license);
    }

    private static Guid? LicenseId(string path, string suffix)
    {
        const string prefix = "/api/admin/licenses/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParse(path[prefix.Length..(path.Length - suffix.Length)], out var id) ? id : null;
    }

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
