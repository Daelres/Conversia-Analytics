using System.Net;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The metrics side of a stand-in API: the answers the dashboard reads, built from a few numbers so each test says only what matters.
/// Plug <see cref="Handle"/> into <see cref="FakeBackend.Data"/>. The conversion endpoint is called twice per load (the period and the one
/// right before it); the call whose <c>to</c> is <see cref="CurrentTo"/> gets <see cref="Conversion"/>, the other one <see cref="PreviousConversion"/>.
/// </summary>
internal sealed class MetricsStub
{
    public static readonly Guid Centro = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    public static readonly Guid Norte = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    public static readonly Guid Ana = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    public static readonly Guid Luis = Guid.Parse("a0000000-0000-4000-8000-000000000002");

    /// <summary>The <c>to</c> (exclusive) of the period under test; set it from the clock the host uses.</summary>
    public DateOnly CurrentTo { get; set; }

    /// <summary>The caller's rights; null makes the caller an owner (who holds everything).</summary>
    public IReadOnlyList<Permission>? MemberPermissions { get; set; }

    public SummaryResponse Summary { get; set; } = null!;

    public ConversionResponse Conversion { get; set; } = null!;

    public ConversionCounts? PreviousConversion { get; set; } = Counts(total: 100, won: 30, lost: 40, inFollowUp: 30, stale: 30, matured: 100, maturedWon: 30, maturedLost: 40);

    public FirstResponseResponse FirstResponse { get; set; } = null!;

    public VolumeResponse Volume { get; set; } = null!;

    public AlertsResponse Alerts { get; set; } = null!;

    public IReadOnlyList<BranchResponse> Branches { get; set; } =
    [
        new() { Id = Centro, Name = "Centro", IsActive = true },
        new() { Id = Norte, Name = "Norte", IsActive = true },
    ];

    /// <summary>Answers a metrics path with this instead (a failure to test); the path is the one without the query, such as <c>/api/metrics/volume</c>.</summary>
    public Dictionary<string, Func<HttpResponseMessage>> Failures { get; } = new();

    private readonly object _gate = new();
    private readonly List<Uri> _metricCalls = [];

    /// <summary>The metrics requests received, in arrival order.</summary>
    public IReadOnlyList<Uri> MetricCalls
    {
        get
        {
            lock (_gate)
            {
                return _metricCalls.ToArray();
            }
        }
    }

    public static MetricsStub Populated(DateOnly currentTo)
    {
        var stub = new MetricsStub { CurrentTo = currentTo };
        var totals = Counts(total: 120, won: 36, lost: 44, inFollowUp: 30, newCount: 10, stale: 20, matured: 100, maturedWon: 36, maturedLost: 44);
        stub.Conversion = new ConversionResponse(
            Period(),
            totals,
            [Row(Centro, "Centro", Counts(total: 80, won: 30, lost: 20, inFollowUp: 20, newCount: 10, stale: 10, matured: 70, maturedWon: 30, maturedLost: 20)),
             Row(Norte, "Norte", Counts(total: 40, won: 6, lost: 24, inFollowUp: 10, newCount: 0, stale: 10, matured: 30, maturedWon: 6, maturedLost: 24))],
            [Row(Ana, "Ana Torres", Counts(total: 60, won: 24, lost: 16, inFollowUp: 10, newCount: 10, stale: 10, matured: 50, maturedWon: 24, maturedLost: 16)),
             Row(Luis, "Luis Pérez", Counts(total: 10, won: 2, lost: 4, inFollowUp: 4, newCount: 0, stale: 2, matured: 8, maturedWon: 2, maturedLost: 4)),
             Row(null, "Sin asesor", Counts(total: 50, won: 10, lost: 24, inFollowUp: 16, newCount: 0, stale: 8, matured: 42, maturedWon: 10, maturedLost: 24))],
            [Row(null, "Ads", Counts(total: 70, won: 30, lost: 20, inFollowUp: 20, newCount: 0, stale: 10, matured: 60, maturedWon: 30, maturedLost: 20)),
             Row(null, "Organic", Counts(total: 40, won: 6, lost: 20, inFollowUp: 14, newCount: 0, stale: 10, matured: 36, maturedWon: 6, maturedLost: 20)),
             Row(null, "Unknown", Counts(total: 10, won: 0, lost: 4, inFollowUp: 6, newCount: 0, stale: 0, matured: 4, maturedWon: 0, maturedLost: 4))]);

        var response = Response(conversations: 120, answered: 100, unansweredOpen: 12, unansweredClosed: 8, median: 14.5, withinSla: 80, overSla: 5, oldest: 190);
        stub.FirstResponse = new FirstResponseResponse(
            Period(),
            response,
            [ResponseRow(Centro, "Centro", Response(80, 70, 6, 4, 9, 88, 2, 60)), ResponseRow(Norte, "Norte", Response(40, 30, 6, 4, 45, 50, 3, 190))],
            [ResponseRow(Ana, "Ana Torres", Response(60, 55, 3, 2, 8, 92, 0, 20)),
             ResponseRow(Luis, "Luis Pérez", Response(10, 8, 1, 1, 70, 40, 1, 90)),
             ResponseRow(null, "Sin asesor", Response(50, 37, 8, 5, 20, 70, 4, 190))],
            [ResponseRow(null, "Ads", Response(70, 60, 6, 4, 10, 85, 2, 50)),
             ResponseRow(null, "Organic", Response(40, 32, 5, 3, 25, 70, 3, 190)),
             ResponseRow(null, "Unknown", Response(10, 8, 1, 1, 30, 60, 0, 10))]);

        stub.Summary = new SummaryResponse(
            Period(), totals, response,
            TotalLost: 44, StatedLost: 30, NoResponse: new NoResponseLoss(14, 31.8),
            TopLossReasons:
            [
                new LossReasonShare("Price", 14, 46.7),
                new LossReasonShare("Timing", 8, 26.7),
                new LossReasonShare("Location", 5, 16.7),
            ]);

        var hours = new List<HourVolume>();
        for (var day = 1; day <= 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                hours.Add(new HourVolume(day, hour, day == 2 && hour == 10 ? 9 : hour is >= 9 and <= 18 ? 2 : 0, 0));
            }
        }

        var days = Enumerable.Range(0, 30).Select(i => new DayVolume(currentTo.AddDays(-30 + i), i == 12 ? 14 : 3 + i % 4, 20, 18)).ToList();
        stub.Volume = new VolumeResponse(Period(), days.Sum(d => d.Conversations), 600, 540, days, hours);

        stub.Alerts = new AlertsResponse(
            Period(),
            DateTime.UtcNow,
            [new MetricAlert(AlertCodes.UnansweredOverSla, AlertSeverities.Critical, "Hay 5 conversaciones esperando respuesta hace más de 30 minutos.", new Dictionary<string, object?> { ["count"] = 5 }),
             new MetricAlert(AlertCodes.BranchBelowAverage, AlertSeverities.Warning, "La sucursal Norte convierte 20 puntos por debajo del promedio.", new Dictionary<string, object?> { ["branchId"] = Norte })],
            new UnansweredSummary(12,
            [
                new UnansweredConversation(Guid.Parse("d0000000-0000-4000-8000-000000000001"), Norte, "Norte", null, null, DateTime.UtcNow.AddMinutes(-190), 190),
                new UnansweredConversation(Guid.Parse("d0000000-0000-4000-8000-000000000002"), Centro, "Centro", Ana, "Ana Torres", DateTime.UtcNow.AddMinutes(-65), 65),
            ]));
        return stub;
    }

    /// <summary>A period with conversations and nothing else: no alert, no unanswered, no loss.</summary>
    public static MetricsStub Quiet(DateOnly currentTo)
    {
        var stub = Populated(currentTo);
        stub.Alerts = new AlertsResponse(Period(), DateTime.UtcNow, [], new UnansweredSummary(0, []));
        var response = Response(conversations: 120, answered: 120, unansweredOpen: 0, unansweredClosed: 0, median: 5, withinSla: 95, overSla: 0, oldest: null);
        stub.FirstResponse = stub.FirstResponse with { Totals = response };
        stub.Summary = stub.Summary with { FirstResponse = response, TotalLost = 0, StatedLost = 0, NoResponse = new NoResponseLoss(0, null), TopLossReasons = [] };
        return stub;
    }

    public static MetricsStub Empty(DateOnly currentTo)
    {
        var stub = Populated(currentTo);
        var none = Counts(total: 0, won: 0, lost: 0, inFollowUp: 0, stale: 0, matured: 0, maturedWon: 0, maturedLost: 0);
        var noResponse = Response(0, 0, 0, 0, null, null, 0, null);
        stub.Conversion = new ConversionResponse(Period(), none, [], [], []);
        stub.PreviousConversion = none;
        stub.FirstResponse = new FirstResponseResponse(Period(), noResponse, [], [], []);
        stub.Summary = new SummaryResponse(Period(), none, noResponse, 0, 0, new NoResponseLoss(0, null), []);
        stub.Volume = new VolumeResponse(Period(), 0, 0, 0, [], []);
        stub.Alerts = new AlertsResponse(Period(), DateTime.UtcNow, [], new UnansweredSummary(0, []));
        return stub;
    }

    /// <summary>Every conversation is too recent for a rate (the period is "in maturation").</summary>
    public static MetricsStub Maturing(DateOnly currentTo)
    {
        var stub = Quiet(currentTo);
        var young = Counts(total: 12, won: 5, lost: 0, inFollowUp: 7, stale: 0, matured: 0, maturedWon: 0, maturedLost: 0);
        stub.Conversion = stub.Conversion with { Totals = young, ByBranch = [Row(Centro, "Centro", young)], ByAdvisor = [Row(null, "Sin asesor", young)], BySource = [Row(null, "Ads", young)] };
        stub.Summary = stub.Summary with { Conversion = young };
        stub.FirstResponse = stub.FirstResponse with
        {
            ByBranch = stub.FirstResponse.ByBranch.Where(r => r.Id == Centro).ToList(),
            ByAdvisor = stub.FirstResponse.ByAdvisor.Where(r => r.Id is null).ToList(),
            BySource = stub.FirstResponse.BySource.Where(r => r.Name == "Ads").ToList(),
        };
        return stub;
    }

    public static ConversionCounts Counts(
        int total, int won, int lost, int inFollowUp, int stale, int matured, int maturedWon, int maturedLost, int newCount = 0)
    {
        var decided = maturedWon + maturedLost + stale;
        return new ConversionCounts
        {
            Total = total, Won = won, Lost = lost, InFollowUp = inFollowUp, New = newCount, Stale = stale,
            InProgress = inFollowUp + newCount - stale, Matured = matured, Immature = total - matured, MaturedWon = maturedWon, MaturedLost = maturedLost,
            Decided = decided, InMaturation = total > 0 && matured == 0,
            ConversionRate = decided == 0 ? null : Math.Round(maturedWon * 100.0 / decided, 1),
            WinShareOfAll = total == 0 ? null : Math.Round(won * 100.0 / total, 1),
        };
    }

    public static FirstResponseStats Response(int conversations, int answered, int unansweredOpen, int unansweredClosed, double? median, double? withinSla, int overSla, double? oldest)
        => new()
        {
            Conversations = conversations, Answered = answered, UnansweredOpen = unansweredOpen, UnansweredClosed = unansweredClosed,
            AvgMinutes = median, MedianMinutes = median, P90Minutes = median, SlaMinutes = 30, WithinSla = answered,
            SlaEligible = answered + overSla + unansweredClosed, AnsweredWithinSlaPercent = withinSla, UnansweredOpenOverSla = overSla, OldestUnansweredOpenMinutes = oldest,
        };

    private static ConversionRow Row(Guid? id, string name, ConversionCounts counts) => new()
    {
        Id = id, Name = name, Total = counts.Total, Won = counts.Won, Lost = counts.Lost, InFollowUp = counts.InFollowUp, New = counts.New, Stale = counts.Stale,
        InProgress = counts.InProgress, Matured = counts.Matured, Immature = counts.Immature, MaturedWon = counts.MaturedWon, MaturedLost = counts.MaturedLost,
        Decided = counts.Decided, InMaturation = counts.InMaturation, ConversionRate = counts.ConversionRate, WinShareOfAll = counts.WinShareOfAll,
    };

    private static FirstResponseRow ResponseRow(Guid? id, string name, FirstResponseStats stats) => new()
    {
        Id = id, Name = name, Conversations = stats.Conversations, Answered = stats.Answered, UnansweredOpen = stats.UnansweredOpen, UnansweredClosed = stats.UnansweredClosed,
        AvgMinutes = stats.AvgMinutes, MedianMinutes = stats.MedianMinutes, P90Minutes = stats.P90Minutes, SlaMinutes = stats.SlaMinutes, WithinSla = stats.WithinSla,
        SlaEligible = stats.SlaEligible, AnsweredWithinSlaPercent = stats.AnsweredWithinSlaPercent, UnansweredOpenOverSla = stats.UnansweredOpenOverSla,
        OldestUnansweredOpenMinutes = stats.OldestUnansweredOpenMinutes,
    };

    private static MetricsPeriod Period() => new(new DateOnly(2026, 9, 19), new DateOnly(2026, 10, 19), "America/Bogota", 30);

    public Task<HttpResponseMessage?> Handle(BackendRequest request)
    {
        var path = request.Path.TrimEnd('/');
        if (request.Method != HttpMethod.Get)
        {
            return Task.FromResult<HttpResponseMessage?>(null);
        }

        if (path == "/api/users/me")
        {
            return Task.FromResult<HttpResponseMessage?>(MemberPermissions is null ? null : FakeBackend.Answer(HttpStatusCode.OK, Member(request)));
        }

        if (path == "/api/branches")
        {
            return Task.FromResult<HttpResponseMessage?>(FakeBackend.Answer(HttpStatusCode.OK, Branches));
        }

        if (!path.StartsWith("/api/metrics/", StringComparison.Ordinal))
        {
            return Task.FromResult<HttpResponseMessage?>(null);
        }

        lock (_gate)
        {
            _metricCalls.Add(request.Uri);
        }

        if (Failures.TryGetValue(path, out var failure))
        {
            return Task.FromResult<HttpResponseMessage?>(failure());
        }

        var isCurrent = ToOf(request.Uri) == CurrentTo;
        object? body = path switch
        {
            "/api/metrics/summary" => Summary,
            "/api/metrics/conversion" when isCurrent => Conversion,
            "/api/metrics/conversion" => PreviousConversion is null ? null : Conversion with { Totals = PreviousConversion },
            "/api/metrics/first-response" => FirstResponse,
            "/api/metrics/volume" => Volume,
            "/api/metrics/alerts" => Alerts,
            _ => null,
        };

        return Task.FromResult<HttpResponseMessage?>(body is null
            ? FakeBackend.Raw(HttpStatusCode.NotFound, null)
            : FakeBackend.Answer(HttpStatusCode.OK, body));
    }

    private UserProfileResponse Member(BackendRequest request)
    {
        var (user, tenant) = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]);
        return new UserProfileResponse { UserId = user, TenantId = tenant, IsOwner = false, Permissions = MemberPermissions! };
    }

    private static DateOnly? ToOf(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return DateOnly.TryParse(query["to"], System.Globalization.CultureInfo.InvariantCulture, out var to) ? to : null;
    }
}
