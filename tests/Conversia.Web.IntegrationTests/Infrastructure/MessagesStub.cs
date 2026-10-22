using System.Net;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Messages;
using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The messages feed side of a stand-in API: the newest messages, the arrival summary and the webhook deliveries the pages read. Plug
/// <see cref="Handle"/> into <see cref="FakeBackend.Data"/>. Every number a customer could be recognised by is a dummy (and masked, as the API does).
/// </summary>
internal sealed class MessagesStub
{
    public static readonly Guid LineCentro = Guid.Parse("11000000-0000-4000-8000-000000000001");
    public static readonly Guid LineNorte = Guid.Parse("11000000-0000-4000-8000-000000000002");
    public static readonly Guid MsgGreeting = Guid.Parse("22000000-0000-4000-8000-000000000001");
    public static readonly Guid MsgReply = Guid.Parse("22000000-0000-4000-8000-000000000002");
    public static readonly Guid MsgImage = Guid.Parse("22000000-0000-4000-8000-000000000003");
    public static readonly Guid MsgMarkup = Guid.Parse("22000000-0000-4000-8000-000000000004");
    public static readonly Guid MsgLong = Guid.Parse("22000000-0000-4000-8000-000000000005");
    public static readonly Guid Conversation = Guid.Parse("33000000-0000-4000-8000-000000000001");

    /// <summary>The number of the customer of <see cref="MsgGreeting"/>: the page must show only its last four digits.</summary>
    public const string CustomerPhone = "573001234567";

    public const string Markup = "<script>alert('x')</script><b>negrita</b> & <img src=x onerror=alert(1)>";

    public static readonly string LongText = string.Join(' ', Enumerable.Range(1, 300).Select(i => $"palabra{i}"));

    public DateTime Now { get; }

    public MessagesStub(DateTime nowUtc)
    {
        Now = nowUtc;
        Messages =
        [
            Item(MsgLong, "Inbound", "text", LongText, Now.AddSeconds(-20), LineNorte, "Daniela", "…0004"),
            Item(MsgMarkup, "Inbound", "text", Markup, Now.AddSeconds(-40), LineCentro, "Cliente <i>raro</i>", "…0003"),
            Item(MsgImage, "Inbound", "image", null, Now.AddMinutes(-1), LineCentro, null, "…0002"),
            Item(MsgReply, "Outbound", "text", "¡Hola Camila!\nLa sesión de axilas cuesta 60 mil.\n¿Te agendo el martes?", Now.AddMinutes(-2), LineCentro, "Camila R.", "…4567"),
            Item(MsgGreeting, "Inbound", "text", "Hola, ¿cuánto cuesta la depilación de axilas?", Now.AddMinutes(-3), LineCentro, "Camila R.", "…4567"),
        ];
        Deliveries =
        [
            Delivery("stored", "Processed", Now.AddSeconds(-20), LineNorte, stored: 1, skipped: 0, receipts: 0),
            Delivery("receipts", "Processed", Now.AddSeconds(-30), LineCentro, stored: 0, skipped: 0, receipts: 2),
            Delivery("skipped", "Processed", Now.AddMinutes(-4), LineCentro, stored: 0, skipped: 1, receipts: 0),
            Delivery("failed", "Failed", Now.AddMinutes(-9), LineCentro, attempts: 5, error: "InvalidOperationException: algo falló con [num] <b>marca</b>"),
            Delivery("pending", "Pending", Now.AddSeconds(-2), LineNorte, attempts: 0),
        ];
        Summary = new MessagesSummaryResponse
        {
            MessagesToday = 5,
            Today = Now.ToString("yyyy-MM-dd"),
            TimeZone = "America/Bogota",
            LastInboundAt = Now.AddSeconds(-20),
            LastOutboundAt = Now.AddMinutes(-2),
            InboundLast24Hours = 4,
            OutboundLast24Hours = 1,
            Lines =
            [
                new LineActivity { ChannelId = LineCentro, DisplayPhoneNumber = "+57 300 555 0100", BranchName = "Centro", IsActive = true, LastDeliveryAt = Now.AddSeconds(-30) },
                new LineActivity { ChannelId = LineNorte, DisplayPhoneNumber = "+57 300 555 0200", BranchName = "Norte", IsActive = true, LastDeliveryAt = Now.AddSeconds(-2) },
                new LineActivity { ChannelId = Guid.Parse("11000000-0000-4000-8000-000000000003"), DisplayPhoneNumber = "+57 300 555 0300", BranchName = "Sur", IsActive = false, LastDeliveryAt = null },
            ],
        };
    }

    /// <summary>Newest first, like the API answers.</summary>
    public IReadOnlyList<RecentMessageItem> Messages { get; set; }

    public IReadOnlyList<WebhookEventItem> Deliveries { get; set; }

    public MessagesSummaryResponse Summary { get; set; }

    public IReadOnlyList<Permission>? MemberPermissions { get; set; }

    public Dictionary<string, Func<BackendRequest, HttpResponseMessage>> Overrides { get; } = new();

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
            ("GET", "/api/users/me") when MemberPermissions is not null => FakeBackend.Answer(HttpStatusCode.OK, MemberWith(request)),
            ("GET", "/api/messages/recent") => Recent(request),
            ("GET", "/api/messages/summary") => FakeBackend.Answer(HttpStatusCode.OK, Summary),
            ("GET", "/api/webhook-events") => Events(request),
            _ => null,
        };
        return Task.FromResult(answer);
    }

    private HttpResponseMessage Recent(BackendRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        var page = int.Parse(query["page"] ?? "1");
        var size = int.Parse(query["pageSize"] ?? "50");
        IEnumerable<RecentMessageItem> items = Messages;
        if (Guid.TryParse(query["channelId"], out var channel))
        {
            items = items.Where(m => m.Line.ChannelId == channel);
        }

        if (query["direction"] is { Length: > 0 } direction)
        {
            items = items.Where(m => string.Equals(m.Direction, direction, StringComparison.OrdinalIgnoreCase));
        }

        if (Guid.TryParse(query["afterId"], out var after))
        {
            var index = Messages.ToList().FindIndex(m => m.Id == after);
            if (index < 0)
            {
                return FakeBackend.Raw(HttpStatusCode.NotFound, """{"title":"Mensaje no encontrado","status":404}""");
            }

            var newer = Messages.Take(index).Select(m => m.Id).ToHashSet();
            items = items.Where(m => newer.Contains(m.Id));
        }

        var all = items.ToList();
        var total = Total >= 0 ? Total : all.Count;
        return FakeBackend.Answer(HttpStatusCode.OK, new PagedResponse<RecentMessageItem> { Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = total });
    }

    private HttpResponseMessage Events(BackendRequest request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        var page = int.Parse(query["page"] ?? "1");
        var size = int.Parse(query["pageSize"] ?? "25");
        IEnumerable<WebhookEventItem> items = Deliveries;
        if (Guid.TryParse(query["channelId"], out var channel))
        {
            items = items.Where(e => e.Line.ChannelId == channel);
        }

        if (query["status"] is { Length: > 0 } status)
        {
            items = items.Where(e => string.Equals(e.Status, status, StringComparison.OrdinalIgnoreCase));
        }

        var all = items.ToList();
        var total = Total >= 0 ? Total : all.Count;
        return FakeBackend.Answer(HttpStatusCode.OK, new PagedResponse<WebhookEventItem> { Items = all.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = total });
    }

    private UserProfileResponse MemberWith(BackendRequest request)
    {
        var (user, tenant) = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]);
        return new UserProfileResponse { UserId = user, TenantId = tenant, IsOwner = false, Permissions = MemberPermissions! };
    }

    private static RecentMessageItem Item(Guid id, string direction, string type, string? text, DateTime sentAt, Guid line, string? name, string masked)
        => new()
        {
            Id = id,
            ConversationId = Conversation,
            SentAt = sentAt,
            ReceivedAt = sentAt.AddSeconds(2),
            Direction = direction,
            Type = type,
            TypeLabel = MessageTypeLabels.For(type),
            Text = text,
            Line = new LineReference { ChannelId = line, DisplayPhoneNumber = line == LineCentro ? "+57 300 555 0100" : "+57 300 555 0200", BranchName = line == LineCentro ? "Centro" : "Norte" },
            Contact = new MessageContact { DisplayName = name, MaskedWaId = masked },
        };

    private static WebhookEventItem Delivery(string outcome, string status, DateTime received, Guid line, int? stored = null, int? skipped = null, int? receipts = null, int attempts = 1, string? error = null)
    {
        var (code, label) = WebhookOutcomes.Describe(Enum.Parse<WebhookEventStatus>(status), error is not null, skipped, receipts, stored, 0);
        return new WebhookEventItem
        {
            Id = Guid.NewGuid(),
            Line = new LineReference { ChannelId = line, DisplayPhoneNumber = line == LineCentro ? "+57 300 555 0100" : "+57 300 555 0200", BranchName = line == LineCentro ? "Centro" : "Norte" },
            ReceivedAt = received,
            Status = status,
            Attempts = attempts,
            ProcessedAt = status == "Processed" ? received.AddSeconds(1) : null,
            SkippedMessageCount = skipped,
            StatusCount = receipts,
            StoredMessageCount = stored,
            DuplicateMessageCount = stored is null ? null : 0,
            OutcomeCode = code,
            Outcome = label,
            LastError = error,
        };
    }
}
