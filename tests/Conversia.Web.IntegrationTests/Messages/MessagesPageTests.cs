using System.Net;
using AngleSharp.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Messages;

/// <summary>GET /mensajes against a stand-in API: the feed, the counters, the lines, the filters, the refresh and every state.</summary>
public class MessagesPageTests
{
    private static IElement Item(PageResponse page, Guid id)
        => page.Document.QuerySelector($"[data-testid=feed-message][data-message-id='{id}']") ?? throw new Xunit.Sdk.XunitException($"No message {id}. Page text: {page.Text}");

    private static IReadOnlyList<Guid> Ids(PageResponse page)
        => page.Select("[data-testid=feed-message]").Select(r => Guid.Parse(r.GetAttribute("data-message-id")!)).ToList();

    // ---- the feed ----

    [Fact]
    public async Task Every_message_is_a_row_newest_first_with_its_direction_customer_line_and_text()
    {
        using var harness = MessagesHarness.Create();

        var page = (await harness.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal([MessagesStub.MsgLong, MessagesStub.MsgMarkup, MessagesStub.MsgImage, MessagesStub.MsgReply, MessagesStub.MsgGreeting], Ids(page));
        var greeting = Item(page, MessagesStub.MsgGreeting);
        Assert.Equal("Inbound", greeting.GetAttribute("data-direction"));
        Assert.Equal("Entrante", greeting.QuerySelector("[data-testid=direction-chip]")!.Plain().Trim());
        Assert.Equal("Camila R.", greeting.QuerySelector("[data-testid=feed-contact]")!.Plain().Trim());
        Assert.Equal("…4567", greeting.QuerySelector("[data-testid=feed-phone]")!.Plain().Trim());
        Assert.Equal("+57 300 555 0100 · Centro", greeting.QuerySelector("[data-testid=feed-line]")!.Plain().Trim());
        Assert.Equal("Hola, ¿cuánto cuesta la depilación de axilas?", greeting.QuerySelector("[data-testid=feed-text]")!.Plain().Trim());
        Assert.Equal("Saliente", Item(page, MessagesStub.MsgReply).QuerySelector("[data-testid=direction-chip]")!.Plain().Trim());
        Assert.Equal("Cliente", Item(page, MessagesStub.MsgImage).QuerySelector("[data-testid=feed-contact]")!.Plain().Trim());
        Assert.Equal($"/conversaciones/{MessagesStub.Conversation}", greeting.QuerySelector("[data-testid=feed-conversation]")!.GetAttribute("href"));
    }

    [Fact]
    public async Task A_message_without_text_says_what_it_is_instead_of_an_empty_line()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        Assert.Equal("[Imagen sin texto]", Item(page, MessagesStub.MsgImage).QuerySelector("[data-testid=feed-text]")!.Plain().Trim());
    }

    [Fact]
    public async Task The_time_is_the_wall_clock_of_the_company_and_the_arrival_is_shown_with_seconds()
    {
        using var harness = MessagesHarness.Create();
        var greeting = harness.Stub.Messages.Single(m => m.Id == MessagesStub.MsgGreeting);
        var local = TimeZoneInfo.ConvertTimeFromUtc(greeting.SentAt, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));
        var arrived = TimeZoneInfo.ConvertTimeFromUtc(greeting.ReceivedAt, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

        var page = await harness.OpenAsync("/mensajes");

        var row = Item(page, MessagesStub.MsgGreeting);
        Assert.Contains(local.ToString("HH:mm:ss"), row.QuerySelector("[data-testid=feed-time]")!.Plain());
        Assert.Equal(greeting.SentAt.ToString("O"), row.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Contains("hace 3 min", row.Plain());
        Assert.Contains("llegó " + arrived.ToString("HH:mm:ss"), row.Plain());
    }

    [Fact]
    public async Task The_page_never_shows_a_whole_phone_number()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        Assert.DoesNotContain(MessagesStub.CustomerPhone, page.Body);
        Assert.DoesNotContain("3001234567", page.Body);
        Assert.DoesNotMatch(@"\d{9,}", page.Document.QuerySelector("[data-testid=feed]")!.Plain());
    }

    // ---- what a customer writes is text, never markup ----

    [Fact]
    public async Task Markup_in_a_message_or_a_name_is_encoded_not_interpreted()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        var row = Item(page, MessagesStub.MsgMarkup);
        Assert.Equal(MessagesStub.Markup, row.QuerySelector("[data-testid=feed-text]")!.TextContent);
        Assert.Empty(row.QuerySelectorAll("script, b, img, i"));
        Assert.Empty(page.Document.QuerySelectorAll("main script"));
        Assert.Equal("Cliente <i>raro</i>", row.QuerySelector("[data-testid=feed-contact]")!.TextContent);
        Assert.DoesNotContain("<script>alert", page.Body);
        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.Contains("&lt;script&gt;alert(", page.Body);
    }

    [Fact]
    public async Task Line_breaks_are_kept_and_a_long_message_is_cut_by_the_stylesheet_not_by_the_data()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        var reply = Item(page, MessagesStub.MsgReply).QuerySelector("[data-testid=feed-text]")!;
        Assert.Equal("¡Hola Camila!\nLa sesión de axilas cuesta 60 mil.\n¿Te agendo el martes?", reply.TextContent);
        // The whole text is in the page (the thread has it too); the look is the stylesheet's: pre-wrap keeps the line breaks, line-clamp cuts the length.
        Assert.Equal(MessagesStub.LongText, Item(page, MessagesStub.MsgLong).QuerySelector("[data-testid=feed-text]")!.TextContent);
        var css = File.ReadAllText(Path.Combine(RepoPaths.WebProject(), "wwwroot", "css", "pages.css"));
        var rule = css[css.IndexOf(".feed__text {", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}')];
        Assert.Contains("white-space: pre-wrap", rule);
        Assert.Contains("-webkit-line-clamp", rule);
        Assert.Contains("overflow: hidden", rule);
    }

    // ---- counters and lines ----

    [Fact]
    public async Task The_strip_answers_is_it_arriving_with_counters_and_the_last_message_of_each_direction()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        string Value(string kpi) => page.Document.QuerySelector($"[data-kpi='{kpi}'] [data-testid=kpi-value]")!.Plain().Trim();
        string Help(string kpi) => page.Document.QuerySelector($"[data-kpi='{kpi}'] .kpi__help")!.Plain().Trim();
        Assert.Equal("5", Value("messages-today"));
        Assert.Contains("America/Bogota", Help("messages-today"));
        Assert.Equal("ahora", Help("last-inbound"));
        Assert.Equal("hace 2 min", Help("last-outbound"));
        Assert.Equal("5", Value("last-24h"));
        Assert.Equal("4 entrantes · 1 salientes", Help("last-24h"));
    }

    [Fact]
    public async Task Every_line_says_when_its_last_delivery_arrived_or_that_none_ever_did()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        var lines = page.Select("[data-testid=line-status]").ToDictionary(l => Guid.Parse(l.GetAttribute("data-channel-id")!));
        Assert.Equal(3, lines.Count);
        Assert.Equal("seen", lines[MessagesStub.LineCentro].GetAttribute("data-delivery"));
        Assert.Matches(@"^Última entrega: Hoy \d\d:\d\d:\d\d · ahora$", lines[MessagesStub.LineCentro].QuerySelector("[data-testid=last-delivery]")!.Plain().Trim());
        var silent = lines.Values.Single(l => l.GetAttribute("data-delivery") == "never");
        Assert.Equal("false", silent.GetAttribute("data-active"));
        Assert.Contains("Nunca ha llegado una entrega", silent.Plain());
        Assert.Contains("Inactiva", silent.Plain());
    }

    // ---- filters, paging, refresh ----

    [Fact]
    public async Task The_filters_in_the_address_are_sent_to_the_api_and_shown_back_in_the_form()
    {
        using var harness = MessagesHarness.Create();

        var page = (await harness.OpenAsync($"/mensajes?linea={MessagesStub.LineCentro}&direccion=entrantes&page=1")).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.FeedCalls, c => c.Path == "/api/messages/recent");
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal(MessagesStub.LineCentro.ToString(), query["channelId"]);
        Assert.Equal("Inbound", query["direction"]);
        Assert.Equal("25", query["pageSize"]);
        var form = page.Document.QuerySelector("[data-testid=message-filters]")!;
        Assert.Equal("get", form.GetAttribute("method"));
        Assert.Equal(MessagesStub.LineCentro.ToString(), form.QuerySelector("select[name=linea] option[selected]")!.GetAttribute("value"));
        Assert.Equal("entrantes", form.QuerySelector("select[name=direccion] option[selected]")!.GetAttribute("value"));
        Assert.Equal([MessagesStub.MsgMarkup, MessagesStub.MsgImage, MessagesStub.MsgGreeting], Ids(page));
        Assert.NotNull(page.Document.QuerySelector("[data-testid=clear-filters]"));
    }

    [Fact]
    public async Task A_hand_edited_address_with_nonsense_shows_the_unfiltered_page()
    {
        using var harness = MessagesHarness.Create();

        var page = (await harness.OpenAsync("/mensajes?linea=zzz&direccion=lateral&page=-2&auto=7&visto=ayer")).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.FeedCalls, c => c.Path == "/api/messages/recent");
        Assert.Equal("?page=1&pageSize=25", call.Uri.Query);
        Assert.Equal(5, Ids(page).Count);
        Assert.Null(page.Document.QuerySelector("[data-testid=clear-filters]"));
        Assert.Empty(page.Select("meta[http-equiv=refresh]"));
    }

    [Fact]
    public async Task The_page_reloads_itself_only_when_asked_and_only_at_a_rate_on_offer()
    {
        using var harness = MessagesHarness.Create();

        var off = await harness.OpenAsync("/mensajes");
        var on = await harness.OpenAsync("/mensajes?auto=10");
        var odd = await harness.OpenAsync("/mensajes?auto=1");

        Assert.Empty(off.Select("meta[http-equiv=refresh]"));
        Assert.Equal("10", Assert.Single(on.Select("meta[http-equiv=refresh]")).GetAttribute("content"));
        Assert.Empty(odd.Select("meta[http-equiv=refresh]"));
        Assert.Equal("true", on.Document.QuerySelector("[data-testid=auto-choice][data-seconds='10']")!.GetAttribute("aria-current"));
        Assert.Equal("true", off.Document.QuerySelector("[data-testid=auto-choice][data-seconds='0']")!.GetAttribute("aria-current"));
    }

    [Fact]
    public async Task The_refresh_controls_are_plain_links_that_keep_the_filters()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync($"/mensajes?linea={MessagesStub.LineNorte}&auto=30");

        var now = page.Document.QuerySelector("[data-testid=refresh-now]")!;
        Assert.Equal($"/mensajes?linea={MessagesStub.LineNorte}&auto=30", now.GetAttribute("href"));
        Assert.Contains("Actualizar", now.Plain());
        var choices = page.Select("[data-testid=auto-choice]").ToDictionary(a => a.GetAttribute("data-seconds")!, a => a.GetAttribute("href")!);
        Assert.Equal(["0", "5", "10", "30"], choices.Keys);
        Assert.Equal($"/mensajes?linea={MessagesStub.LineNorte}", choices["0"]);
        Assert.Equal($"/mensajes?linea={MessagesStub.LineNorte}&auto=5", choices["5"]);
        Assert.Equal("Apagado", page.Document.QuerySelector("[data-testid=auto-choice][data-seconds='0']")!.Plain().Trim());
        Assert.Equal("30", page.Document.QuerySelector("[data-testid=message-filters] input[type=hidden][name=auto]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Messages_that_arrived_after_the_one_marked_as_seen_are_flagged_new()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync($"/mensajes?visto={MessagesStub.MsgReply}");

        var call = Assert.Single(harness.FeedCalls, c => c.Uri.Query.Contains("afterId"));
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal(MessagesStub.MsgReply.ToString(), query["afterId"]);
        Assert.Equal("100", query["pageSize"]);
        var flagged = page.Select("[data-testid=feed-message][data-new=true]").Select(r => Guid.Parse(r.GetAttribute("data-message-id")!)).ToList();
        Assert.Equal([MessagesStub.MsgLong, MessagesStub.MsgMarkup, MessagesStub.MsgImage], flagged);
        Assert.Equal(3, page.Select("[data-testid=new-chip]").Count());
        Assert.Contains("3 nuevos", page.Document.QuerySelector("[data-testid=new-count]")!.Plain());
    }

    [Fact]
    public async Task Marking_as_seen_links_to_the_newest_message_and_an_unknown_marker_is_ignored()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes?auto=5");
        var unknown = (await harness.OpenAsync($"/mensajes?visto={Guid.NewGuid()}")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal($"/mensajes?auto=5&visto={MessagesStub.MsgLong}", page.Document.QuerySelector("[data-testid=mark-seen]")!.GetAttribute("href"));
        Assert.Empty(page.Select("[data-testid=new-chip]"));
        Assert.Equal(5, Ids(unknown).Count);
        Assert.Empty(unknown.Select("[data-testid=new-chip]"));
    }

    [Fact]
    public async Task A_long_list_is_paged_with_links_that_keep_the_filters()
    {
        using var harness = MessagesHarness.Create(configure: stub => stub.Total = 60);

        var page = (await harness.OpenAsync("/mensajes?direccion=salientes&page=2")).ShouldBe(HttpStatusCode.OK);

        var pager = page.Document.QuerySelector("[data-testid=pager]")!;
        Assert.Equal("/mensajes?direccion=salientes", pager.QuerySelector("[data-testid=pager-prev]")!.GetAttribute("href"));
        Assert.Equal("/mensajes?direccion=salientes&page=3", pager.QuerySelector("[data-testid=pager-next]")!.GetAttribute("href"));
        Assert.Contains("Página 2 de 3", pager.Plain());
    }

    // ---- states ----

    [Fact]
    public async Task With_nothing_arrived_the_empty_state_says_what_to_check_and_links_to_the_deliveries()
    {
        using var harness = MessagesHarness.Create(configure: stub => stub.Messages = []);

        var page = (await harness.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Aún no llegan mensajes", page.Text);
        Assert.Contains("guía de pruebas", page.Text);
        Assert.Contains("el webhook esté registrado", page.Text);
        Assert.Contains("la línea activa", page.Text);
        Assert.Contains(page.Select("[data-testid=empty-state] a"), a => a.GetAttribute("href") == "/mensajes/eventos");
        Assert.Empty(page.Select("[data-testid=feed-message]"));
    }

    [Fact]
    public async Task A_filter_with_no_match_offers_to_clear_it()
    {
        using var harness = MessagesHarness.Create(configure: stub => stub.Messages = []);

        var page = await harness.OpenAsync($"/mensajes?linea={MessagesStub.LineNorte}");

        Assert.Contains("Ningún mensaje coincide", page.Text);
    }

    [Fact]
    public async Task A_member_without_the_conversations_permission_gets_a_friendly_403()
    {
        using var harness = MessagesHarness.Create([Permission.MetricsView]);

        var page = (await harness.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("Sin acceso a los mensajes", page.Text);
        Assert.Empty(page.Select("[data-testid=feed-message]"));
    }

    [Fact]
    public async Task A_member_with_the_permission_sees_the_page()
    {
        using var harness = MessagesHarness.Create([Permission.ConversationsView]);

        var page = (await harness.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(5, Ids(page).Count);
    }

    [Fact]
    public async Task A_company_that_cannot_operate_is_sent_to_the_pending_page()
    {
        using var harness = MessagesHarness.Create();
        harness.Stub.Overrides["GET /api/messages/recent"] = _ =>
            FakeBackend.Raw(HttpStatusCode.Forbidden, """{"status":403,"title":"Empresa no activa","detail":"Tu empresa está pendiente de activación.","code":"tenant_not_active","tenantStatus":"Pending"}""");

        (await harness.OpenAsync("/mensajes")).ShouldRedirectTo("/cuenta-pendiente");
    }

    [Fact]
    public async Task A_throttled_api_is_a_friendly_wait_message_and_a_failing_one_a_503()
    {
        using var throttled = MessagesHarness.Create();
        throttled.Stub.Overrides["GET /api/messages/recent"] = _ => FakeBackend.Raw(HttpStatusCode.TooManyRequests, """{"title":"Demasiadas solicitudes","status":429}""", retryAfter: "21");
        using var failing = MessagesHarness.Create();
        failing.Stub.Overrides["GET /api/messages/summary"] = _ => FakeBackend.Raw(HttpStatusCode.BadGateway, null);

        var wait = await throttled.OpenAsync("/mensajes");
        var down = (await failing.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.ServiceUnavailable);

        Assert.Contains("21 segundos", wait.Text);
        Assert.Empty(wait.Select("[data-testid=feed-message]"));
        Assert.Contains("no está disponible", down.Text);
    }

    [Fact]
    public async Task An_expired_session_and_an_anonymous_visitor_go_to_the_login()
    {
        using var harness = MessagesHarness.Create();
        harness.Stub.Overrides["GET /api/messages/recent"] = _ => FakeBackend.Raw(HttpStatusCode.Unauthorized, null);

        (await harness.OpenAsync("/mensajes")).ShouldRedirectToLogin();
        using var anonymous = harness.Host.NewSession();
        Assert.Equal("/mensajes?auto=10", (await anonymous.GetAsync("/mensajes?auto=10")).ShouldRedirectToLogin());
    }

    // ---- shape ----

    [Fact]
    public async Task The_page_is_one_h1_in_the_shell_with_its_own_entry_next_to_the_conversations()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes");

        Assert.Single(page.Select("main h1"));
        Assert.Equal("messages", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
        Assert.Equal("page", page.Document.QuerySelector("nav a[href='/mensajes']")!.GetAttribute("aria-current"));
        var nav = page.Select("nav a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal(nav.IndexOf("/conversaciones") + 1, nav.IndexOf("/mensajes"));
        Assert.Contains(page.Select("main a[href]"), a => a.GetAttribute("href") == "/mensajes/eventos");
        Assert.Empty(page.Select("[style]"));
    }

    [Fact]
    public async Task All_the_text_of_the_page_is_spanish()
    {
        using var harness = MessagesHarness.Create();
        var page = await harness.OpenAsync("/mensajes");
        using var emptyHarness = MessagesHarness.Create(configure: s => s.Messages = []);
        var empty = await emptyHarness.OpenAsync("/mensajes");

        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(empty)));
    }

    [Fact]
    public async Task The_conversation_list_links_to_the_messages_page()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        Assert.Equal("/mensajes", page.Document.QuerySelector("[data-testid=to-messages]")!.GetAttribute("href"));
    }
}
