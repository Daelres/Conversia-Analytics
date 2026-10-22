using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>GET /api/messages/recent and /api/messages/summary on a real PostgreSQL: order, filters, cursor, counters and what is hidden.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class MessageFeedApiTests
{
    private const string RecentUrl = "/api/messages/recent";
    private const string SummaryUrl = "/api/messages/summary";

    private static readonly DateTime D0 = new(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MessageFeedApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<ChannelFixture> NewTenantAsync() => StartChannelAsync(_factory, _client);

    private static async Task<PagedResponse<RecentMessageItem>> RecentAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(query.Length == 0 ? RecentUrl : $"{RecentUrl}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<RecentMessageItem>>())!;
    }

    // ---- order, paging, shape -------------------------------------------------------------------

    [Fact]
    public async Task TheFeed_IsNewestFirst_AcrossConversations_AndPaged()
    {
        using var f = await NewTenantAsync();
        await SeedConversationAsync(_factory.Services, f, D0, [In(0, "uno"), Out(60, "dos")]);
        await SeedConversationAsync(_factory.Services, f, D0.AddMinutes(10), [In(0, "tres"), In(30, "cuatro")]);

        var all = await RecentAsync(f.Tenant.Owner.Client);
        var second = await RecentAsync(f.Tenant.Owner.Client, "pageSize=3&page=2");

        Assert.Equal(["cuatro", "tres", "dos", "uno"], all.Items.Select(i => i.Text));
        Assert.Equal(4, all.TotalCount);
        Assert.Equal(["uno"], second.Items.Select(i => i.Text));
        Assert.Equal((2, 3, 4), (second.Page, second.PageSize, second.TotalCount));
    }

    [Fact]
    public async Task AnItem_CarriesTheLine_TheMaskedCustomer_TheDirection_AndTheArrivalTime()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedConversationAsync(_factory.Services, f, D0, [In(0, "Hola"), new Msg(MessageDirection.Inbound, 5, null, "image")], waId: "573001234567");

        var items = (await RecentAsync(f.Tenant.Owner.Client)).Items;

        var image = items[0];
        Assert.Equal(("Inbound", "image", "Imagen"), (image.Direction, image.Type, image.TypeLabel));
        Assert.Null(image.Text);
        var text = items[1];
        Assert.Equal(("Hola", "Texto", D0), (text.Text, text.TypeLabel, text.SentAt));
        Assert.Equal(seeded.ConversationId, text.ConversationId);
        Assert.Equal((f.Channel.Id, "+57 300 555 0100", f.Branch.Name), (text.Line.ChannelId, text.Line.DisplayPhoneNumber, text.Line.BranchName));
        Assert.Equal(("Cliente", "…4567"), (text.Contact.DisplayName, text.Contact.MaskedWaId));
        Assert.True(Math.Abs((text.ReceivedAt - DateTime.UtcNow).TotalMinutes) < 5, "ReceivedAt is when the service stored the message");
    }

    [Fact]
    public async Task TheWholeNumberOfTheCustomerIsNeverInTheAnswer()
    {
        using var f = await NewTenantAsync();
        await SeedConversationAsync(_factory.Services, f, D0, [In(0, "Hola")], waId: "573001234567");

        var body = await (await f.Tenant.Owner.Client.GetAsync(RecentUrl)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("573001234567", body);
        Assert.DoesNotContain("3001234567", body);
        Assert.Contains("…4567", body);
        Assert.Contains("\"direction\":\"Inbound\"", body);
    }

    // ---- filters and cursor ----------------------------------------------------------------------

    [Fact]
    public async Task Filters_ByLine_Direction_AndSince()
    {
        using var f = await NewTenantAsync();
        var other = await AddChannelAsync(f, "pnid-2", "+57 300 555 0222");
        await SeedConversationAsync(_factory.Services, f, D0, [In(0, "vieja"), Out(30, "respuesta")]);
        await SeedConversationAsync(_factory.Services, f.TenantId, f.Branch.Id, other.Id, D0.AddHours(2), [In(0, "otra línea")]);
        var client = f.Tenant.Owner.Client;

        Assert.Equal(["otra línea"], (await RecentAsync(client, $"channelId={other.Id}")).Items.Select(i => i.Text));
        Assert.Equal(["respuesta"], (await RecentAsync(client, "direction=outbound")).Items.Select(i => i.Text));
        Assert.Equal(["otra línea", "vieja"], (await RecentAsync(client, "direction=Inbound")).Items.Select(i => i.Text));
        Assert.Equal(["otra línea"], (await RecentAsync(client, $"since={Uri.EscapeDataString(D0.AddHours(1).ToString("O"))}")).Items.Select(i => i.Text));
        Assert.Empty((await RecentAsync(client, $"channelId={Guid.NewGuid()}")).Items);
    }

    [Fact]
    public async Task TheCursor_ReturnsOnlyWhatIsNewerThanTheLastSeenMessage_EvenWithinTheSameSecond()
    {
        using var f = await NewTenantAsync();
        await SeedConversationAsync(_factory.Services, f, D0, [In(0, "a"), In(0, "b"), In(0, "c")]);
        var client = f.Tenant.Owner.Client;
        var ordered = (await RecentAsync(client)).Items; // newest first, ties broken by id
        var middle = ordered[1];

        var newer = await RecentAsync(client, $"afterId={middle.Id}");
        var none = await RecentAsync(client, $"afterId={ordered[0].Id}");

        Assert.Equal([ordered[0].Id], newer.Items.Select(i => i.Id));
        Assert.Equal(1, newer.TotalCount);
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task AnUnknownCursor_AndOneOfAnotherCompany_AreTheSame404()
    {
        using var f = await NewTenantAsync();
        using var other = await NewTenantAsync();
        await SeedConversationAsync(_factory.Services, other, D0, [In(0, "ajeno")]);
        var foreign = (await RecentAsync(other.Tenant.Owner.Client)).Items[0].Id;

        var unknown = await f.Tenant.Owner.Client.GetAsync($"{RecentUrl}?afterId={Guid.NewGuid()}");
        var crossTenant = await f.Tenant.Owner.Client.GetAsync($"{RecentUrl}?afterId={foreign}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(Strip(await unknown.Content.ReadAsStringAsync()), Strip(await crossTenant.Content.ReadAsStringAsync()));
        Assert.Contains("Mensaje no encontrado", Strip(await unknown.Content.ReadAsStringAsync()));
    }

    private static string Strip(string problem)
    {
        using var document = JsonDocument.Parse(problem);
        return document.RootElement.GetProperty("title").GetString() + "|" + document.RootElement.GetProperty("detail").GetString();
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("direction=Sideways")]
    [InlineData("since=ayer")]
    public async Task AnInvalidQuery_IsAnSpanish400(string query)
    {
        using var f = await NewTenantAsync();

        var response = await f.Tenant.Owner.Client.GetAsync($"{RecentUrl}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- tenants and permissions -----------------------------------------------------------------

    [Fact]
    public async Task ACompanySeesOnlyItsOwnMessages()
    {
        using var mine = await NewTenantAsync();
        using var theirs = await NewTenantAsync();
        await SeedConversationAsync(_factory.Services, mine, D0, [In(0, "mío")]);
        await SeedConversationAsync(_factory.Services, theirs, D0, [In(0, "suyo")]);

        Assert.Equal(["mío"], (await RecentAsync(mine.Tenant.Owner.Client)).Items.Select(i => i.Text));
        Assert.Equal(["suyo"], (await RecentAsync(theirs.Tenant.Owner.Client)).Items.Select(i => i.Text));
        // A line of the other company is as good as no line.
        Assert.Empty((await RecentAsync(mine.Tenant.Owner.Client, $"channelId={theirs.Channel.Id}")).Items);
    }

    [Theory]
    [InlineData(RecentUrl)]
    [InlineData(SummaryUrl)]
    public async Task ItNeedsConversationsView_AndASignedInUser(string url)
    {
        using var f = await NewTenantAsync();
        using var none = await f.Tenant.AddMemberAsync("none");
        using var others = await f.Tenant.AddMemberAsync("others", Permission.OrganizationView, Permission.MetricsView, Permission.ConversationsManage);
        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.ConversationsView);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await others.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData(RecentUrl)]
    [InlineData(SummaryUrl)]
    [InlineData("/api/webhook-events")]
    public async Task APendingCompany_IsRefusedWithTenantNotActive(string url)
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending", pending: true);

        var response = await tenant.Owner.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("tenant_not_active", body.RootElement.GetProperty("code").GetString());
    }

    // ---- the summary ------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummary_CountsTodayInTheCompanyTimeZone_AndTheLast24Hours()
    {
        using var f = await NewTenantAsync();
        var zone = new TenantTimeZone(TenantTimeZone.DefaultId);
        var now = DateTime.UtcNow;
        var startOfToday = zone.StartOfDayUtc(zone.LocalDate(now));
        var yesterdayLate = startOfToday.AddMinutes(-1); // yesterday for the company, still inside the last 24 hours
        var recent = now.AddMinutes(-5);
        await SeedConversationAsync(_factory.Services, f, yesterdayLate, [In(0, "ayer")]);
        await SeedConversationAsync(_factory.Services, f, recent, [In(0, "hoy"), Out(1, "hoy 2")]);
        await SeedConversationAsync(_factory.Services, f, now.AddDays(-3), [In(0, "antigua"), Out(10, "antigua 2")]);

        var summary = (await f.Tenant.Owner.Client.GetFromJsonAsync<MessagesSummaryResponse>(SummaryUrl))!;

        var seededToday = new[] { recent, recent.AddSeconds(1) }.Count(t => t >= startOfToday);
        Assert.Equal(seededToday, summary.MessagesToday);
        Assert.Equal((zone.LocalDate(now).ToString("yyyy-MM-dd"), "America/Bogota"), (summary.Today, summary.TimeZone));
        Assert.Equal((2, 1), (summary.InboundLast24Hours, summary.OutboundLast24Hours)); // yesterday's late message and today's two; the old pair is out
    }

    [Fact]
    public async Task TheSummary_LastInboundAndOutbound_AndPerLineLastDelivery()
    {
        using var f = await NewTenantAsync();
        var quiet = await AddChannelAsync(f, "pnid-quiet", "+57 300 555 0333");
        await SeedConversationAsync(_factory.Services, f, D0, [In(0, "hola"), Out(60, "respuesta"), In(120, "gracias")]);
        await ClearQueueAsync(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: "wamid.SUMMARY-1", timestamp: Dialog360Samples.T0));

        var summary = (await f.Tenant.Owner.Client.GetFromJsonAsync<MessagesSummaryResponse>(SummaryUrl))!;

        Assert.Equal((D0.AddSeconds(120), D0.AddSeconds(60)), (summary.LastInboundAt, summary.LastOutboundAt));
        Assert.Equal(2, summary.Lines.Count);
        var active = summary.Lines.Single(l => l.ChannelId == f.Channel.Id);
        Assert.NotNull(active.LastDeliveryAt);
        Assert.Equal(f.Branch.Name, active.BranchName);
        var silent = summary.Lines.Single(l => l.ChannelId == quiet.Id);
        Assert.Null(silent.LastDeliveryAt);
        Assert.True(silent.IsActive);
    }

    [Fact]
    public async Task AnEmptyCompany_HasZeroCountersAndNoLastTimes()
    {
        using var f = await NewTenantAsync();

        var summary = (await f.Tenant.Owner.Client.GetFromJsonAsync<MessagesSummaryResponse>(SummaryUrl))!;
        var recent = await RecentAsync(f.Tenant.Owner.Client);

        Assert.Equal((0, 0, 0), (summary.MessagesToday, summary.InboundLast24Hours, summary.OutboundLast24Hours));
        Assert.Null(summary.LastInboundAt);
        Assert.Null(summary.LastOutboundAt);
        Assert.Empty(recent.Items);
        Assert.Equal(0, recent.TotalCount);
    }
}
