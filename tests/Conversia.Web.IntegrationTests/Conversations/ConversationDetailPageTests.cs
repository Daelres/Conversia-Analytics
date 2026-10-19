using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Conversia.Application.DTOs.Conversations;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>GET and POST /conversaciones/{id} against a stand-in API: the chat, the opportunity, the two manager forms and every state.</summary>
public class ConversationDetailPageTests
{
    private static string Url(Guid id) => $"/conversaciones/{id}";

    private static IHtmlFormElement AssignForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "assign-form", "assign-advisor form");

    private static IHtmlFormElement OutcomeForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "outcome-form", "set-outcome form");

    private static IReadOnlyList<BackendRequest> Writes(ConversationsHarness harness) => harness.Backend.DataRequests.Where(r => r.Method == HttpMethod.Put).ToList();

    private static JsonElement BodyOf(BackendRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    private static ConversationsHarness WithMessages(IReadOnlyList<MessageResponse> messages, IReadOnlyList<Permission>? member = null)
        => ConversationsHarness.Create(member, stub => stub.Messages = messages);

    private static MessageResponse Message(string direction, string? text, string utc, string type = "text")
        => new() { Id = Guid.NewGuid(), Direction = direction, Type = type, Text = text, SentAt = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) };

    // ---- the chat ----

    [Fact]
    public async Task The_chat_lists_the_messages_oldest_first_with_the_customer_and_the_business_on_opposite_sides()
    {
        using var harness = WithMessages(
        [
            Message("Inbound", "Hola, quiero depilarme", "2026-10-18T14:05:00Z"),
            Message("Outbound", "¡Claro! ¿Qué zona?", "2026-10-18T14:09:00Z"),
        ]);

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);

        var messages = page.Select("[data-testid=chat-message]").ToList();
        Assert.Equal(["Inbound", "Outbound"], messages.Select(m => m.GetAttribute("data-direction")));
        Assert.Contains("chat__item--in", messages[0].ClassName);
        Assert.Contains("chat__item--out", messages[1].ClassName);
        Assert.Contains("Cliente", messages[0].Plain());
        Assert.Contains("Negocio", messages[1].Plain());
        Assert.Equal("Hola, quiero depilarme", messages[0].QuerySelector(".chat__text")!.Plain().Trim());
        Assert.Equal("¡Claro! ¿Qué zona?", messages[1].QuerySelector(".chat__text")!.Plain().Trim());
    }

    [Fact]
    public async Task Times_are_shown_in_the_time_of_the_business_and_a_new_day_gets_a_separator()
    {
        using var harness = WithMessages(
        [
            Message("Inbound", "Buenas noches", "2026-10-18T03:30:00Z"), // 22:30 of the 17th in Bogota
            Message("Outbound", "Buenos días", "2026-10-18T14:05:00Z"), // 09:05 of the 18th
        ]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.Equal(["sáb 17 oct", "dom 18 oct"], page.Select("[data-testid=chat-day]").Select(d => d.Plain().Trim()));
        Assert.Equal(["22:30", "09:05"], page.Select("[data-testid=chat-message] time").Select(t => t.Plain().Trim()));
        Assert.Equal("2026-10-18T03:30:00.0000000Z", page.Select("[data-testid=chat-message] time").First().GetAttribute("datetime"));
    }

    [Fact]
    public async Task Media_without_a_caption_is_a_placeholder_and_message_text_is_never_html()
    {
        using var harness = WithMessages(
        [
            Message("Inbound", null, "2026-10-18T14:05:00Z", "image"),
            Message("Inbound", "<script>alert(1)</script> <b>negrita</b>", "2026-10-18T14:06:00Z"),
        ]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.Equal("[Imagen sin texto]", page.Select(".chat__text--empty").Single().Plain().Trim());
        Assert.Empty(page.Select("[data-testid=chat] script, [data-testid=chat] b"));
        Assert.Contains("<script>alert(1)</script> <b>negrita</b>", page.Select("[data-testid=chat-message] .chat__text").Last().Plain());
    }

    [Fact]
    public async Task A_conversation_longer_than_the_api_returns_says_so()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Messages = [Message("Inbound", "Hola", "2026-10-18T14:05:00Z")]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won)); // the list says 12 messages, the thread carries one

        Assert.Contains("Se muestran los primeros 1 de 12 mensajes", page.Document.QuerySelector("[data-testid=chat-truncated]")!.Plain());
    }

    [Fact]
    public async Task A_conversation_without_messages_says_so()
    {
        using var harness = WithMessages([]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.NotNull(page.Document.QuerySelector("[data-testid=chat-empty]"));
    }

    // ---- the header and the opportunity ----

    [Fact]
    public async Task The_header_names_the_customer_and_the_detail_may_show_the_whole_number()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.Equal("Camila R.", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Contains($"WhatsApp {ConversationsStub.WonPhone}", page.Document.QuerySelector(".page-header__desc")!.Plain());
        var anonymous = await harness.OpenAsync(Url(ConversationsStub.Waiting));
        Assert.Equal("Cliente …0001", anonymous.Document.QuerySelector("main h1")!.Plain().Trim());
    }

    [Fact]
    public async Task An_automatic_stage_shows_the_classifier_confidence_and_what_it_understood()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        var card = page.Document.QuerySelector("[data-testid=opportunity]")!;
        Assert.Equal("Auto", card.QuerySelector("[data-testid=stage-source]")!.GetAttribute("data-stage-source"));
        Assert.Contains("Automática", card.QuerySelector("[data-testid=stage-source]")!.Plain());
        Assert.Equal("92 %", card.QuerySelector("[data-testid=confidence]")!.Plain().Trim());
        Assert.Equal("0.92", card.QuerySelector("[data-testid=confidence]")!.GetAttribute("data-value"));
        Assert.Equal("depilacion_laser", card.QuerySelector("[data-testid=service]")!.Plain().Trim());
        Assert.Equal("Ganada", card.QuerySelector("[data-testid=stage-chip]")!.Plain().Trim());
        Assert.Null(card.QuerySelector("[data-testid=stage-changed-by]"));
        Assert.Contains("intent.booking", card.QuerySelector("[data-testid=rules]")!.Plain());
    }

    [Fact]
    public async Task A_manual_stage_says_so_and_names_who_changed_it_by_id_only()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Manual));

        var card = page.Document.QuerySelector("[data-testid=opportunity]")!;
        Assert.Equal("Manual", card.QuerySelector("[data-testid=stage-source]")!.GetAttribute("data-stage-source"));
        var by = card.QuerySelector("[data-testid=stage-changed-by]")!;
        Assert.Equal(ConversationsStub.Editor.ToString(), by.GetAttribute("data-user-id"));
        Assert.Contains("e0000000", by.Plain());
        Assert.DoesNotContain(ConversationsStub.Editor.ToString(), by.Plain()); // eight characters, not the whole id
    }

    [Fact]
    public async Task A_lost_sale_shows_its_reason_and_objection_in_spanish()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Lost));

        var card = page.Document.QuerySelector("[data-testid=opportunity]")!;
        Assert.Equal("Precio", card.QuerySelector("[data-testid=loss-reason]")!.Plain().Trim());
        Assert.Equal("Precio", card.QuerySelector("[data-testid=objection]")!.Plain().Trim());
        Assert.Equal("80 %", card.QuerySelector("[data-testid=confidence]")!.Plain().Trim());
    }

    [Fact]
    public async Task The_assigned_advisor_and_who_assigned_them_are_shown()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        var facts = page.Document.QuerySelector("[data-testid=conversation-facts]")!;
        Assert.Contains("Ana Torres", facts.QuerySelector("[data-testid=assigned-advisor]")!.Plain());
        Assert.Equal(ConversationsStub.Editor.ToString(), facts.QuerySelector("[data-testid=advisor-assigned-by]")!.GetAttribute("data-user-id"));
        var unassigned = await harness.OpenAsync(Url(ConversationsStub.Waiting));
        Assert.Equal("Sin asignar", unassigned.Document.QuerySelector("[data-testid=advisor-chip]")!.Plain().Trim());
        Assert.Null(unassigned.Document.QuerySelector("[data-testid=advisor-assigned-by]"));
    }

    // ---- who may change things ----

    [Fact]
    public async Task A_viewer_sees_the_forms_hidden_and_no_submit_buttons()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView]);

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);

        Assert.True(page.Document.QuerySelector("[data-testid=manage-panel]")!.HasAttribute("hidden"));
        Assert.Empty(page.Select("[data-testid=manage-panel] button[type=submit]"));
        Assert.NotNull(page.Document.QuerySelector("[data-testid=chat]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Permission.ConversationsManage)]
    public async Task A_manager_and_an_owner_see_the_forms(Permission? manage)
    {
        using var harness = ConversationsHarness.Create(manage is null ? null : [Permission.ConversationsView, manage.Value]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.False(page.Document.QuerySelector("[data-testid=manage-panel]")!.HasAttribute("hidden"));
        Assert.Equal(2, page.Select("[data-testid=manage-panel] button[type=submit]").Count());
    }

    [Fact]
    public async Task A_member_without_the_conversations_permission_gets_a_friendly_403()
    {
        using var harness = ConversationsHarness.Create([Permission.MetricsView]);

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("Conversaciones", page.Text);
        Assert.Empty(page.Select("[data-testid=chat-message]"));
    }

    [Fact]
    public async Task A_forged_post_from_a_viewer_reaches_the_api_and_is_refused_there()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView]);
        harness.Stub.Overrides[$"PUT /api/conversations/{ConversationsStub.Won}/advisor"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));
        var form = FormModel.From(page, AssignForm(page)).Set("AdvisorId", ConversationsStub.Luis.ToString());

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Single(Writes(harness));
        Assert.Contains("permiso", response.Text);
    }

    // ---- assigning an advisor ----

    [Fact]
    public async Task Only_the_active_advisors_of_the_branch_are_offered_and_the_current_one_is_selected()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Won)); // Centro, assigned to Ana

        var options = page.Select("#assign-advisor option").ToList();
        Assert.Equal(["Sin asesor", "Ana Torres", "Luis Pérez"], options.Select(o => o.Plain().Trim()));
        Assert.Equal(ConversationsStub.Ana.ToString(), options.Single(o => o.HasAttribute("selected")).GetAttribute("value"));
    }

    [Fact]
    public async Task Assigning_an_advisor_puts_it_to_the_api_and_lands_back_with_a_confirmation()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));
        var form = FormModel.From(page, AssignForm(page)).Set("AdvisorId", ConversationsStub.Luis.ToString());

        var response = await session.SubmitAsync(form);

        var put = Assert.Single(Writes(harness));
        Assert.Equal($"/api/conversations/{ConversationsStub.Won}/advisor", put.Path);
        Assert.Equal(ConversationsStub.Luis, BodyOf(put).GetProperty("advisorId").GetGuid());
        response.ShouldRedirectTo($"{Url(ConversationsStub.Won)}?notice=advisor");
        var after = await session.FollowAsync(response);
        Assert.Contains("Asesor actualizado", after.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }

    [Fact]
    public async Task Choosing_no_advisor_unassigns_the_conversation()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));

        var response = await session.SubmitAsync(FormModel.From(page, AssignForm(page)).Set("AdvisorId", ""));

        response.ShouldRedirectTo($"{Url(ConversationsStub.Won)}?notice=advisor");
        Assert.Equal(JsonValueKind.Null, BodyOf(Assert.Single(Writes(harness))).GetProperty("advisorId").ValueKind);
    }

    [Fact]
    public async Task An_advisor_id_that_is_not_an_id_never_reaches_the_api()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));

        var response = await session.SubmitAsync(FormModel.From(page, AssignForm(page)).Set("AdvisorId", "ana'; drop table"));

        Assert.Empty(Writes(harness));
        Assert.Contains("Elige un asesor de la lista", response.Text);
    }

    [Fact]
    public async Task An_advisor_of_another_branch_is_refused_by_the_api_and_the_message_is_shown()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"PUT /api/conversations/{ConversationsStub.Won}/advisor"] = _ => FakeBackend.Raw(
            HttpStatusCode.BadRequest, """{"title":"x","status":400,"detail":"El asesor pertenece a otra sucursal."}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));

        var response = await session.SubmitAsync(FormModel.From(page, AssignForm(page)).Set("AdvisorId", ConversationsStub.Marta.ToString()));

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("El asesor pertenece a otra sucursal.", response.Text);
        Assert.NotNull(response.Document.QuerySelector("[data-testid=chat]"));
    }

    // ---- deciding the outcome ----

    [Fact]
    public async Task Marking_a_conversation_won_sends_only_the_stage()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));
        var form = FormModel.From(page, OutcomeForm(page)).Set("Stage", "Won").Set("Detail", "no debería viajar");

        var response = await session.SubmitAsync(form);

        var put = Assert.Single(Writes(harness));
        Assert.Equal($"/api/conversations/{ConversationsStub.Waiting}/opportunity", put.Path);
        var body = BodyOf(put);
        Assert.Equal("Won", body.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lossReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("detail").ValueKind);
        response.ShouldRedirectTo($"{Url(ConversationsStub.Waiting)}?notice=outcome");
        Assert.Contains("Resultado guardado", (await session.FollowAsync(response)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }

    [Fact]
    public async Task Marking_a_conversation_lost_sends_the_reason_and_the_detail()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));
        var form = FormModel.From(page, OutcomeForm(page)).Set("Stage", "Lost").Set("LossReason", "Price").Set("Detail", "  Dijo que estaba caro  ");

        var response = await session.SubmitAsync(form);

        var body = BodyOf(Assert.Single(Writes(harness)));
        Assert.Equal("Lost", body.GetProperty("stage").GetString());
        Assert.Equal("Price", body.GetProperty("lossReason").GetString());
        Assert.Equal("Dijo que estaba caro", body.GetProperty("detail").GetString());
        Assert.True(response.IsRedirect);
    }

    [Fact]
    public async Task A_lost_sale_without_a_reason_is_refused_before_reaching_the_api()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));

        var response = await session.SubmitAsync(FormModel.From(page, OutcomeForm(page)).Set("Stage", "Lost").Set("LossReason", ""));

        Assert.Empty(Writes(harness));
        Assert.Contains("Elige por qué se perdió la venta.", response.Text);
        Assert.NotNull(response.Document.QuerySelector("[data-testid=chat]"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Banana", "")]
    [InlineData("Won", "Price")]
    [InlineData("Lost", "Banana")]
    public async Task A_stage_or_reason_that_makes_no_sense_never_reaches_the_api(string stage, string reason)
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));

        await session.SubmitAsync(FormModel.From(page, OutcomeForm(page)).Set("Stage", stage).Set("LossReason", reason));

        Assert.Empty(Writes(harness));
    }

    [Fact]
    public async Task A_detail_over_the_limit_is_refused_locally()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));

        var response = await session.SubmitAsync(FormModel.From(page, OutcomeForm(page)).Set("Stage", "Lost").Set("LossReason", "Other").Set("Detail", new string('a', 501)));

        Assert.Empty(Writes(harness));
        Assert.Contains("500", response.Text);
    }

    [Fact]
    public async Task A_stale_write_is_a_conflict_message_with_a_way_to_reload()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"PUT /api/conversations/{ConversationsStub.Waiting}/opportunity"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, null);
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Waiting));

        var response = await session.SubmitAsync(FormModel.From(page, OutcomeForm(page)).Set("Stage", "Won"));

        Assert.Contains("Otra persona modificó esta conversación", response.Document.QuerySelector("[data-testid=notice-modified]")!.Plain());
        Assert.Equal(Url(ConversationsStub.Waiting), response.Document.QuerySelector("[data-testid=notice-modified] a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task The_form_starts_from_the_current_outcome_of_a_lost_conversation()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Lost));

        Assert.Equal("Lost", page.Select("#outcome-stage option[selected]").Single().GetAttribute("value"));
        Assert.Equal("Price", page.Select("#outcome-reason option[selected]").Single().GetAttribute("value"));
        var fresh = await harness.OpenAsync(Url(ConversationsStub.Waiting)); // a "New" conversation has no outcome to start from
        Assert.DoesNotContain(fresh.Select("#outcome-stage option[selected]"), o => o.GetAttribute("value") != "");
    }

    // ---- states ----

    [Fact]
    public async Task An_unknown_conversation_is_a_404_that_says_what_is_missing()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync(Url(Guid.NewGuid()))).ShouldBe(HttpStatusCode.NotFound);

        Assert.Contains("Conversación no encontrada", page.Text);
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/conversaciones");
        Assert.Empty(page.Select("[data-testid=chat]"));
    }

    [Fact]
    public async Task A_malformed_id_is_the_generic_not_found_page()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync("/conversaciones/not-a-guid")).ShouldBe(HttpStatusCode.NotFound);

        Assert.DoesNotContain("Conversación no encontrada", page.Text);
    }

    [Fact]
    public async Task A_throttled_or_failing_api_gets_the_same_friendly_messages_as_every_page()
    {
        using var throttled = ConversationsHarness.Create();
        throttled.Stub.Overrides[$"GET /api/conversations/{ConversationsStub.Won}"] = _ => FakeBackend.Raw(HttpStatusCode.TooManyRequests, """{"title":"x","status":429}""", retryAfter: "12");
        using var failing = ConversationsHarness.Create();
        failing.Stub.Overrides[$"GET /api/conversations/{ConversationsStub.Won}"] = _ => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, null);

        Assert.Contains("12 segundos", (await throttled.OpenAsync(Url(ConversationsStub.Won))).Text);
        Assert.Contains("no está disponible", (await failing.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.ServiceUnavailable).Text);
    }

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_and_back()
    {
        using var harness = ConversationsHarness.Create();
        using var anonymous = harness.Host.NewSession();

        var response = await anonymous.GetAsync(Url(ConversationsStub.Won));

        Assert.Equal(Url(ConversationsStub.Won), response.ShouldRedirectToLogin());
    }

    [Fact]
    public async Task The_page_is_one_h1_with_the_conversations_entry_active_and_all_its_text_in_spanish()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        Assert.Single(page.Select("main h1"));
        Assert.Equal("conversation", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
        Assert.Equal("page", page.Document.QuerySelector("nav a[href='/conversaciones']")!.GetAttribute("aria-current"));
        Assert.Empty(page.Select("[style]"));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        Assert.NotNull(page.FindLogoutForm());
    }
}
