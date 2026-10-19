using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The conversations side of a stand-in API: a list and a detail the pages read, the two writes and the branches and advisors that go with
/// them. Plug <see cref="Handle"/> into <see cref="FakeBackend.Data"/>. Every number a customer could be recognised by is a dummy.
/// </summary>
internal sealed class ConversationsStub
{
    public static readonly Guid Centro = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    public static readonly Guid Norte = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    public static readonly Guid Ana = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    public static readonly Guid Luis = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    public static readonly Guid Marta = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    public static readonly Guid Retired = Guid.Parse("a0000000-0000-4000-8000-000000000004");
    public static readonly Guid Waiting = Guid.Parse("d0000000-0000-4000-8000-000000000001");
    public static readonly Guid Won = Guid.Parse("d0000000-0000-4000-8000-000000000002");
    public static readonly Guid ClosedSilent = Guid.Parse("d0000000-0000-4000-8000-000000000003");
    public static readonly Guid Lost = Guid.Parse("d0000000-0000-4000-8000-000000000004");
    public static readonly Guid Manual = Guid.Parse("d0000000-0000-4000-8000-000000000005");
    public static readonly Guid Editor = Guid.Parse("e0000000-0000-4000-8000-000000000001");

    /// <summary>The number of the customer in <see cref="Won"/>; the list must never show it whole.</summary>
    public const string WonPhone = "573001234567";

    public DateTime Now { get; }

    public ConversationsStub(DateTime nowUtc)
    {
        Now = nowUtc;
        All =
        [
            Item(Waiting, "573009990001", null, Norte, "Norte", null, null, "Ads", "Open", Now.AddMinutes(-190), Now.AddMinutes(-190), null, null, Stage("New", "Auto"), 3),
            Item(Won, WonPhone, "Camila R.", Centro, "Centro", Ana, "Ana Torres", "Organic", "Closed", Now.AddDays(-3), Now.AddDays(-3), Now.AddDays(-3).AddMinutes(9), Now.AddDays(-2), Stage("Won", "Auto", 0.92m, service: "depilacion_laser"), 12),
            Item(ClosedSilent, "573009990003", null, Norte, "Norte", null, null, "Unknown", "Closed", Now.AddDays(-12), Now.AddDays(-12), null, Now.AddDays(-5), Stage("Lost", "Auto", 0.4m, loss: "NoResponse"), 1),
            Item(Lost, "573009990004", "Daniela", Centro, "Centro", Luis, "Luis Pérez", "Ads", "Closed", Now.AddDays(-6), Now.AddDays(-6), Now.AddDays(-6).AddMinutes(75), Now.AddDays(-5), Stage("Lost", "Auto", 0.8m, loss: "Price", objection: "Price"), 8),
            Item(Manual, "573009990005", null, Centro, "Centro", Ana, "Ana Torres", "Ads", "Open", Now.AddHours(-5), Now.AddHours(-5), Now.AddHours(-5).AddMinutes(1), null, Stage("InFollowUp", "Manual", 0.6m, changedBy: Editor), 6),
        ];
    }

    public IReadOnlyList<ConversationListItem> All { get; set; }

    public IReadOnlyList<BranchResponse> Branches { get; set; } =
    [
        new() { Id = Centro, Name = "Centro", IsActive = true },
        new() { Id = Norte, Name = "Norte", IsActive = true },
    ];

    public IReadOnlyList<AdvisorResponse> Advisors { get; set; } =
    [
        new() { Id = Ana, Name = "Ana Torres", BranchId = Centro, IsActive = true },
        new() { Id = Luis, Name = "Luis Pérez", BranchId = Centro, IsActive = true },
        new() { Id = Marta, Name = "Marta Gil", BranchId = Norte, IsActive = true },
        new() { Id = Retired, Name = "Pablo Viejo", BranchId = Centro, IsActive = false },
    ];

    /// <summary>The caller's rights; null makes the caller an owner.</summary>
    public IReadOnlyList<Permission>? MemberPermissions { get; set; }

    /// <summary>The messages of the conversation the detail page opens, oldest first.</summary>
    public IReadOnlyList<MessageResponse>? Messages { get; set; }

    /// <summary>Answers these paths (with their method, <c>PUT /api/conversations/...</c>) with something else.</summary>
    public Dictionary<string, Func<BackendRequest, HttpResponseMessage>> Overrides { get; } = new();

    /// <summary>What the write endpoints answered, for the assertions that follow a post.</summary>
    public int Total { get; set; } = -1;

    public Task<HttpResponseMessage?> Handle(BackendRequest request)
    {
        var path = request.Path.TrimEnd('/');
        var key = $"{request.Method.Method} {path}";
        if (Overrides.TryGetValue(key, out var custom))
        {
            return Task.FromResult<HttpResponseMessage?>(custom(request));
        }

        HttpResponseMessage? answer = (request.Method.Method, path) switch
        {
            ("GET", "/api/users/me") when MemberPermissions is not null => FakeBackend.Answer(HttpStatusCode.OK, Member(request)),
            ("GET", "/api/branches") => FakeBackend.Answer(HttpStatusCode.OK, Branches),
            ("GET", "/api/advisors") => FakeBackend.Answer(HttpStatusCode.OK, Advisors),
            ("GET", "/api/conversations") => List(request),
            ("GET", _) when IdOf(path, "") is { } id => Detail(id),
            ("PUT", _) when IdOf(path, "/advisor") is { } id => Assign(id, request),
            ("PUT", _) when IdOf(path, "/opportunity") is { } id => Outcome(id, request),
            _ => null,
        };
        return Task.FromResult(answer);
    }

    private HttpResponseMessage List(BackendRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        var page = int.Parse(query["page"] ?? "1");
        var size = int.Parse(query["pageSize"] ?? "20");
        IEnumerable<ConversationListItem> items = All;
        if (Guid.TryParse(query["branchId"], out var branch))
        {
            items = items.Where(c => c.BranchId == branch);
        }

        if (query["stage"] is { Length: > 0 } stage)
        {
            items = items.Where(c => c.Opportunity.Stage == stage);
        }

        if (query["unanswered"] == "true")
        {
            items = items.Where(c => c.Unanswered);
        }

        var all = items.ToList();
        var total = Total >= 0 ? Total : all.Count;
        return FakeBackend.Answer(HttpStatusCode.OK, new PagedResponse<ConversationListItem> { Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = total });
    }

    private HttpResponseMessage Detail(Guid id)
    {
        var item = All.FirstOrDefault(c => c.Id == id);
        if (item is null)
        {
            return FakeBackend.Raw(HttpStatusCode.NotFound, null);
        }

        var messages = Messages ?? DefaultMessages(item);
        return FakeBackend.Answer(HttpStatusCode.OK, ToDetail(item, messages));
    }

    private HttpResponseMessage Assign(Guid id, BackendRequest request)
    {
        var item = All.FirstOrDefault(c => c.Id == id);
        return item is null ? FakeBackend.Raw(HttpStatusCode.NotFound, null) : FakeBackend.Answer(HttpStatusCode.OK, item);
    }

    private HttpResponseMessage Outcome(Guid id, BackendRequest request)
    {
        var item = All.FirstOrDefault(c => c.Id == id);
        return item is null ? FakeBackend.Raw(HttpStatusCode.NotFound, null) : FakeBackend.Answer(HttpStatusCode.OK, item.Opportunity);
    }

    private UserProfileResponse Member(BackendRequest request)
    {
        var (user, tenant) = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]);
        return new UserProfileResponse { UserId = user, TenantId = tenant, IsOwner = false, Permissions = MemberPermissions! };
    }

    private static Guid? IdOf(string path, string suffix)
    {
        const string prefix = "/api/conversations/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var middle = path[prefix.Length..(path.Length - suffix.Length)];
        return Guid.TryParse(middle, out var id) ? id : null;
    }

    private IReadOnlyList<MessageResponse> DefaultMessages(ConversationListItem item)
    {
        var start = item.StartedAt;
        return
        [
            Message("Inbound", "Hola, quiero información de la depilación láser", start),
            Message("Outbound", "¡Hola! Con gusto. ¿Para qué zona la necesitas?", start.AddMinutes(9)),
            Message("Inbound", "Axilas y piernas", start.AddMinutes(11)),
            Message("Outbound", "La sesión de axilas cuesta 60 mil. ¿Te agendo el martes a las 4 pm?", start.AddMinutes(14)),
            new MessageResponse { Id = Guid.NewGuid(), Direction = "Inbound", Type = "image", Text = null, SentAt = start.AddDays(1) },
        ];
    }

    private static MessageResponse Message(string direction, string text, DateTime sentAt)
        => new() { Id = Guid.NewGuid(), Direction = direction, Type = "text", Text = text, SentAt = sentAt };

    private static ConversationDetailResponse ToDetail(ConversationListItem item, IReadOnlyList<MessageResponse> messages) => new()
    {
        Id = item.Id, Contact = item.Contact, ChannelId = item.ChannelId, BranchId = item.BranchId, BranchName = item.BranchName, AdvisorId = item.AdvisorId,
        AdvisorName = item.AdvisorName, Source = item.Source, SourceReference = item.SourceReference, Status = item.Status, StartedAt = item.StartedAt,
        LastMessageAt = item.LastMessageAt, FirstInboundAt = item.FirstInboundAt, FirstResponseAt = item.FirstResponseAt, ClosedAt = item.ClosedAt,
        MessageCount = Math.Max(item.MessageCount, messages.Count), Opportunity = item.Opportunity, Messages = messages,
        AdvisorAssignedBy = item.AdvisorId is null ? null : Editor, AdvisorAssignedAt = item.AdvisorId is null ? null : item.StartedAt.AddMinutes(30),
    };

    private static OpportunityResponse Stage(
        string stage, string source, decimal? confidence = null, string? service = null, string? loss = null, string objection = "None", Guid? changedBy = null)
        => new()
        {
            Stage = stage, StageSource = source, StageChangedAt = changedBy is null ? null : DateTime.UtcNow.AddHours(-4), StageChangedBy = changedBy,
            ServiceInterest = service, Objection = objection, LossReason = loss, ClassificationConfidence = confidence,
            ClassifiedAt = confidence is null ? null : DateTime.UtcNow.AddDays(-1), ClassificationRules = confidence is null ? null : "intent.booking,outcome.confirmed",
        };

    private static ConversationListItem Item(
        Guid id, string waId, string? name, Guid branch, string branchName, Guid? advisor, string? advisorName, string source, string status,
        DateTime started, DateTime? inbound, DateTime? response, DateTime? closed, OpportunityResponse opportunity, int messages)
        => new()
        {
            Id = id, Contact = new ContactSummary { Id = Guid.NewGuid(), WaId = waId, DisplayName = name }, ChannelId = Guid.NewGuid(), BranchId = branch,
            BranchName = branchName, AdvisorId = advisor, AdvisorName = advisorName, Source = source, Status = status, StartedAt = started,
            LastMessageAt = (closed ?? response ?? inbound ?? started), FirstInboundAt = inbound, FirstResponseAt = response, ClosedAt = closed,
            MessageCount = messages, Opportunity = opportunity,
        };
}
