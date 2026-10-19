using System.Net;
using AngleSharp.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>GET /conversaciones against a stand-in API: the rows, what is hidden of the customer, the filters, the pager and every state.</summary>
public class ConversationListPageTests
{
    private static IElement Row(PageResponse page, Guid id)
        => page.Document.QuerySelector($"[data-testid=conversation-row][data-conversation-id='{id}']") ?? throw new Xunit.Sdk.XunitException($"No row for {id}. Page text: {page.Text}");

    private static IReadOnlyList<Guid> RowIds(PageResponse page)
        => page.Select("[data-testid=conversation-row]").Select(r => Guid.Parse(r.GetAttribute("data-conversation-id")!)).ToList();

    private static string Query(BackendRequest call) => call.Uri.Query;

    // ---- rows ----

    [Fact]
    public async Task Every_conversation_is_a_row_with_its_stage_origin_branch_and_advisor()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync("/conversaciones")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(5, RowIds(page).Count);
        var won = Row(page, ConversationsStub.Won);
        Assert.Equal("Won", won.GetAttribute("data-stage"));
        Assert.Equal("Ganada", won.QuerySelector("[data-testid=stage-chip]")!.Plain().Trim());
        Assert.Equal("Orgánico", won.QuerySelector("[data-testid=source-chip]")!.Plain().Trim());
        Assert.Equal("Centro", won.QuerySelector("[data-testid=conversation-branch]")!.Plain().Trim());
        Assert.Equal("Ana Torres", won.QuerySelector("[data-testid=conversation-advisor]")!.Plain().Trim());
        Assert.Equal("Sin asignar", Row(page, ConversationsStub.Waiting).QuerySelector("[data-testid=conversation-advisor]")!.Plain().Trim());
        Assert.Equal("Anuncios", Row(page, ConversationsStub.Waiting).QuerySelector("[data-testid=source-chip]")!.Plain().Trim());
        Assert.Equal("Perdida", Row(page, ConversationsStub.Lost).QuerySelector("[data-testid=stage-chip]")!.Plain().Trim());
        Assert.Equal("En seguimiento", Row(page, ConversationsStub.Manual).QuerySelector("[data-testid=stage-chip]")!.Plain().Trim());
        Assert.Equal("12", won.Plain().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last());
    }

    [Fact]
    public async Task The_list_never_shows_a_phone_number_only_its_last_four_digits()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        Assert.DoesNotContain(ConversationsStub.WonPhone, page.Body);
        Assert.DoesNotContain("5730012345", page.Body);
        Assert.DoesNotContain("573009990001", page.Body);
        Assert.Equal("…4567", Row(page, ConversationsStub.Won).QuerySelector("[data-testid=conversation-phone]")!.Plain().Trim());
        Assert.Equal("Camila R.", Row(page, ConversationsStub.Won).QuerySelector("[data-testid=conversation-link]")!.Plain().Trim());
        Assert.Equal("Cliente …0001", Row(page, ConversationsStub.Waiting).QuerySelector("[data-testid=conversation-link]")!.Plain().Trim());
        Assert.DoesNotMatch(@"\d{7,}", page.Document.QuerySelector("[data-testid=conversation-table]")!.Plain());
    }

    [Fact]
    public async Task The_first_response_column_tells_how_long_it_took_or_that_nobody_answered()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        var answered = Row(page, ConversationsStub.Won).QuerySelector("[data-testid=reply-chip]")!;
        Assert.Equal("answered", answered.GetAttribute("data-reply"));
        Assert.Equal("9", answered.GetAttribute("data-minutes"));
        Assert.Equal("9 min", answered.Plain().Trim());
        var slow = Row(page, ConversationsStub.Lost).QuerySelector("[data-testid=reply-chip]")!;
        Assert.Equal("1 h 15 min", slow.Plain().Trim());
        var waiting = Row(page, ConversationsStub.Waiting).QuerySelector("[data-testid=reply-chip]")!;
        Assert.Equal("waiting", waiting.GetAttribute("data-reply"));
        Assert.Contains("Sin responder · 3 h 10 min", waiting.Plain().Trim());
        var closed = Row(page, ConversationsStub.ClosedSilent).QuerySelector("[data-testid=reply-chip]")!;
        Assert.Equal("closed-unanswered", closed.GetAttribute("data-reply"));
        Assert.Contains("Se cerró sin respuesta", closed.Plain());
    }

    [Fact]
    public async Task Each_row_shows_when_it_started_and_how_long_ago_and_links_to_its_detail()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        var won = Row(page, ConversationsStub.Won);
        Assert.Contains("hace 3 d · cerrada", won.Plain());
        Assert.Contains("hace 3 h", Row(page, ConversationsStub.Waiting).Plain()); // 190 minutes
        Assert.Equal($"/conversaciones/{ConversationsStub.Won}", won.QuerySelector("a[data-testid=conversation-link]")!.GetAttribute("href"));
    }

    // ---- filters ----

    [Fact]
    public async Task The_filters_in_the_address_are_sent_to_the_api_and_shown_back_in_the_form()
    {
        using var harness = ConversationsHarness.Create();
        var url = $"/conversaciones?branch={ConversationsStub.Centro}&advisor={ConversationsStub.Ana}&stage=Won&source=Organic&status=Closed&from=2026-09-01&to=2026-10-01&reply=closed-unanswered";

        var page = (await harness.OpenAsync(url)).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.ConversationCalls, c => c.Path == "/api/conversations");
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal(ConversationsStub.Centro.ToString(), query["branchId"]);
        Assert.Equal(ConversationsStub.Ana.ToString(), query["advisorId"]);
        Assert.Equal("Won", query["stage"]);
        Assert.Equal("Organic", query["source"]);
        Assert.Equal("Closed", query["status"]);
        Assert.Equal("2026-09-01", query["from"]);
        Assert.Equal("2026-10-01", query["to"]);
        Assert.Equal("true", query["closedUnanswered"]);
        Assert.Null(query["unanswered"]);
        Assert.Equal("1", query["page"]);
        Assert.Equal("20", query["pageSize"]);

        var form = page.Document.QuerySelector("[data-testid=conversation-filters]")!;
        Assert.Equal("get", form.GetAttribute("method"));
        Assert.Equal(ConversationsStub.Centro.ToString(), form.QuerySelector("select[name=branch] option[selected]")!.GetAttribute("value"));
        Assert.Equal(ConversationsStub.Ana.ToString(), form.QuerySelector("select[name=advisor] option[selected]")!.GetAttribute("value"));
        Assert.Equal("Won", form.QuerySelector("select[name=stage] option[selected]")!.GetAttribute("value"));
        Assert.Equal("Organic", form.QuerySelector("select[name=source] option[selected]")!.GetAttribute("value"));
        Assert.Equal("Closed", form.QuerySelector("select[name=status] option[selected]")!.GetAttribute("value"));
        Assert.Equal("closed-unanswered", form.QuerySelector("select[name=reply] option[selected]")!.GetAttribute("value"));
        Assert.Equal("2026-09-01", form.QuerySelector("input[name=from]")!.GetAttribute("value"));
        Assert.Equal("2026-10-01", form.QuerySelector("input[name=to]")!.GetAttribute("value"));
        Assert.NotNull(page.Document.QuerySelector("[data-testid=clear-filters]"));
    }

    [Fact]
    public async Task The_panel_alert_link_lands_on_the_list_of_open_unanswered_conversations()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync("/conversaciones?reply=unanswered&stage=New")).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.ConversationCalls, c => c.Path == "/api/conversations");
        Assert.Contains("unanswered=true", Query(call));
        Assert.DoesNotContain("closedUnanswered", Query(call));
        Assert.Equal([ConversationsStub.Waiting], RowIds(page));
        Assert.Equal("unanswered", page.Document.QuerySelector("select[name=reply] option[selected]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task The_unassigned_option_asks_the_api_for_conversations_without_an_advisor()
    {
        using var harness = ConversationsHarness.Create();

        await harness.OpenAsync("/conversaciones?advisor=none");

        var call = Assert.Single(harness.ConversationCalls, c => c.Path == "/api/conversations");
        Assert.Contains("unassigned=true", Query(call));
        Assert.DoesNotContain("advisorId", Query(call));
    }

    [Fact]
    public async Task A_hand_edited_address_with_nonsense_shows_the_unfiltered_list()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync("/conversaciones?branch=zzz&stage=Banana&from=ayer&page=-4&reply=maybe")).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.ConversationCalls, c => c.Path == "/api/conversations");
        Assert.Equal("?page=1&pageSize=20", Query(call));
        Assert.Equal(5, RowIds(page).Count);
        Assert.Null(page.Document.QuerySelector("[data-testid=clear-filters]"));
    }

    [Fact]
    public async Task The_branch_and_advisor_selects_list_the_organization_with_an_unassigned_option()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        var branches = page.Select("select[name=branch] option").Select(o => o.Plain().Trim()).ToList();
        Assert.Equal(["Todas", "Centro", "Norte"], branches);
        var advisors = page.Select("select[name=advisor] option").Select(o => o.Plain().Trim()).ToList();
        Assert.Equal(["Todos", "Sin asignar", "Ana Torres", "Luis Pérez", "Marta Gil", "Pablo Viejo (inactivo)"], advisors);
        Assert.Equal(["", "New", "InFollowUp", "Won", "Lost"], page.Select("select[name=stage] option").Select(o => o.GetAttribute("value")!));
        Assert.Equal(["Todas", "Nueva", "En seguimiento", "Ganada", "Perdida"], page.Select("select[name=stage] option").Select(o => o.Plain().Trim()));
    }

    [Fact]
    public async Task Without_access_to_the_organization_the_page_works_with_no_branch_or_advisor_filters()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView]);
        harness.Stub.Overrides["GET /api/branches"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);
        harness.Stub.Overrides["GET /api/advisors"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);

        var page = (await harness.OpenAsync($"/conversaciones?branch={ConversationsStub.Centro}")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(3, RowIds(page).Count);
        Assert.Empty(page.Select("select[name=branch]"));
        Assert.Empty(page.Select("select[name=advisor]"));
        Assert.Equal(ConversationsStub.Centro.ToString(), page.Document.QuerySelector("input[type=hidden][name=branch]")!.GetAttribute("value"));
    }

    // ---- paging ----

    [Fact]
    public async Task A_long_list_is_paged_with_links_that_keep_the_filters()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Total = 45);

        var page = (await harness.OpenAsync("/conversaciones?stage=Won&page=2")).ShouldBe(HttpStatusCode.OK);

        var pager = page.Document.QuerySelector("[data-testid=pager]")!;
        Assert.Equal("2", pager.GetAttribute("data-page"));
        Assert.Equal("3", pager.GetAttribute("data-page-count"));
        Assert.Equal("45", pager.GetAttribute("data-total-count"));
        Assert.Equal("/conversaciones?stage=Won", pager.QuerySelector("[data-testid=pager-prev]")!.GetAttribute("href"));
        Assert.Equal("/conversaciones?stage=Won&page=3", pager.QuerySelector("[data-testid=pager-next]")!.GetAttribute("href"));
        Assert.Contains("Página 2 de 3", pager.Plain());
        Assert.Contains("45 conversaciones", pager.Plain());
    }

    [Fact]
    public async Task The_first_and_last_pages_have_no_link_where_there_is_nothing_to_go_to()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        Assert.False(page.Document.QuerySelector("[data-testid=pager-prev]")!.HasAttribute("href"));
        Assert.False(page.Document.QuerySelector("[data-testid=pager-next]")!.HasAttribute("href"));
        Assert.Contains("Página 1 de 1", page.Document.QuerySelector("[data-testid=pager]")!.Plain());
    }

    // ---- states ----

    [Fact]
    public async Task A_list_without_conversations_is_an_empty_state_and_a_filtered_one_offers_to_clear_the_filters()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.All = []);

        var empty = (await harness.OpenAsync("/conversaciones")).ShouldBe(HttpStatusCode.OK);
        var filtered = (await harness.OpenAsync("/conversaciones?stage=Won")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Aún no hay conversaciones", empty.Text);
        Assert.Contains(empty.Select("[data-testid=empty-state] a"), a => a.GetAttribute("href") == "/organizacion");
        Assert.Contains("Ninguna conversación coincide", filtered.Text);
        Assert.Contains(filtered.Select("[data-testid=empty-state] a"), a => a.GetAttribute("href") == "/conversaciones");
        Assert.Empty(empty.Select("[data-testid=conversation-row]"));
    }

    [Fact]
    public async Task A_member_without_the_conversations_permission_gets_a_friendly_403_and_no_call()
    {
        using var harness = ConversationsHarness.Create([Permission.MetricsView]);

        var page = (await harness.OpenAsync("/conversaciones")).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("Conversaciones", page.Text);
        Assert.Empty(page.Select("[data-testid=conversation-row]"));
    }

    [Fact]
    public async Task A_throttled_api_is_a_friendly_wait_message()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides["GET /api/conversations"] = _ => FakeBackend.Raw(HttpStatusCode.TooManyRequests, """{"title":"Demasiadas solicitudes","status":429}""", retryAfter: "21");

        var page = await harness.OpenAsync("/conversaciones");

        Assert.Contains("21 segundos", page.Text);
        Assert.Empty(page.Select("[data-testid=conversation-row]"));
        Assert.NotNull(page.Document.QuerySelector("[data-testid=conversation-filters]"));
    }

    [Fact]
    public async Task An_api_failure_is_a_503_with_the_unavailable_message()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides["GET /api/conversations"] = _ => FakeBackend.Raw(HttpStatusCode.BadGateway, null);

        var page = (await harness.OpenAsync("/conversaciones")).ShouldBe(HttpStatusCode.ServiceUnavailable);

        Assert.Contains("no está disponible", page.Text);
    }

    [Fact]
    public async Task A_filter_the_api_refuses_is_explained_in_the_apis_own_words()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides["GET /api/conversations"] = _ => FakeBackend.Raw(
            HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"From":["El campo Desde debe ser anterior al campo Hasta."]}}""");

        var page = (await harness.OpenAsync("/conversaciones?from=2026-10-10&to=2026-10-01")).ShouldBe(HttpStatusCode.BadRequest);

        Assert.Contains("El campo Desde debe ser anterior al campo Hasta.", page.Text);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=conversation-filters]"));
    }

    [Fact]
    public async Task An_expired_session_goes_back_to_the_login_and_an_anonymous_visitor_too()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides["GET /api/conversations"] = _ => FakeBackend.Raw(HttpStatusCode.Unauthorized, null);

        (await harness.OpenAsync("/conversaciones")).ShouldRedirectToLogin();
        using var anonymous = harness.Host.NewSession();
        Assert.Equal("/conversaciones?stage=Won", (await anonymous.GetAsync("/conversaciones?stage=Won")).ShouldRedirectToLogin());
    }

    // ---- shape ----

    [Fact]
    public async Task The_page_is_one_h1_in_the_shell_with_its_own_entry_in_the_navigation()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync("/conversaciones");

        Assert.Single(page.Select("main h1"));
        Assert.Equal("conversations", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
        Assert.Equal("page", page.Document.QuerySelector("nav a[href='/conversaciones']")!.GetAttribute("aria-current"));
        var nav = page.Select("nav a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.True(nav.IndexOf("/panel") < nav.IndexOf("/conversaciones"));
        Assert.Empty(page.Select("[style]"));
        Assert.NotNull(page.FindLogoutForm());
    }

    [Fact]
    public async Task All_the_text_of_the_page_is_spanish()
    {
        using var harness = ConversationsHarness.Create();
        var page = await harness.OpenAsync("/conversaciones");

        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
    }
}
