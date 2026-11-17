using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Conversia.Application.DTOs.Inbox;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.IntegrationTests.Organization;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>
/// The composer with the customer service window (docs/MESSAGING.md): a countdown while free text is allowed, the template picker once it is
/// not, the Spanish words of the new refusals, the read receipt on opening, and the inbox page with its polled list.
/// </summary>
public class WindowAndTemplateComposerTests
{
    private static readonly Guid Open = ConversationsStub.Manual;

    private static string Url(Guid id) => $"/conversaciones/{id}";

    private static IReadOnlyList<BackendRequest> Posts(ConversationsHarness harness, string suffix)
        => harness.Backend.DataRequests.Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith(suffix, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task With_the_window_open_the_text_composer_shows_how_long_free_text_is_still_allowed()
    {
        using var harness = ConversationsHarness.Create();

        var page = await harness.OpenAsync(Url(Open));

        var countdown = page.Document.QuerySelector("[data-testid=composer-window]")!;
        Assert.Contains("Puedes escribir texto libre durante", countdown.Plain());
        Assert.Null(page.Document.QuerySelector("[data-testid=composer-window-closed]"));
        Assert.False(page.FindForm(f => f.GetAttribute("data-testid") == "composer-form", "composer").ClosestHidden());
        Assert.True(page.Document.QuerySelector("[data-testid=template-composer]")!.HasAttribute("hidden"));
        Assert.DoesNotContain(harness.Backend.DataRequests, r => r.Path.EndsWith("/templates", StringComparison.Ordinal)); // nothing to offer
    }

    [Fact]
    public async Task With_the_window_closed_free_text_is_not_offered_and_the_sendable_templates_are()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.WindowOpen = false);

        var page = await harness.OpenAsync(Url(Open));

        Assert.Contains("Pasaron más de 24 horas", page.Document.QuerySelector("[data-testid=composer-window-closed]")!.Plain());
        Assert.True(page.FindForm(f => f.GetAttribute("data-testid") == "composer-form", "composer").ClosestHidden());
        var picker = page.Document.QuerySelector("[data-testid=template-composer]")!;
        Assert.False(picker.HasAttribute("hidden"));
        var options = picker.QuerySelectorAll("#template-pick option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["recordatorio_cita|es"], options); // the pending one is not offered
    }

    [Fact]
    public async Task Picking_a_template_shows_its_category_the_charge_note_and_one_field_per_placeholder_and_sending_posts_the_values()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.WindowOpen = false);
        var session = await harness.SignInAsync();

        var page = await session.GetAsync($"{Url(Open)}?plantilla=recordatorio_cita%7Ces");

        Assert.Contains("Utilidad", page.Document.QuerySelector("[data-testid=template-category]")!.Plain());
        Assert.Contains("Meta cobra cada mensaje de plantilla", page.Document.QuerySelector("[data-testid=template-charge-note]")!.Plain());
        var form = page.FindForm(f => f.GetAttribute("data-testid") == "template-form", "template form");
        Assert.Equal(2, form.QuerySelectorAll("input[name^='Input.Values']").Length);

        var answer = await session.SubmitAsync(FormModel.From(page, form).Set("Input.Values[0]", "Laura").Set("Input.Values[1]", "lunes"));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        var sent = Assert.Single(harness.Stub.SentTemplates);
        Assert.Equal(("recordatorio_cita", "es"), (sent.TemplateName, sent.Language));
        Assert.Equal(["Laura", "lunes"], sent.Parameters);
    }

    [Theory]
    [InlineData(SendFailure.WindowClosed, "Pasaron más de 24 horas")]
    [InlineData(SendFailure.TemplateUnavailable, "La plantilla elegida ya no está disponible")]
    public async Task The_new_refusals_have_their_own_Spanish_words(string code, string words)
    {
        Assert.Contains(code, SendFailure.SendCodes);
        Assert.StartsWith(words, SendFailure.Describe(code));

        using var harness = ConversationsHarness.Create(configure: stub =>
        {
            stub.WindowOpen = false;
            stub.Overrides[$"POST /api/conversations/{Open}/template-messages"] = _ =>
                FakeBackend.Raw(HttpStatusCode.Conflict, $$"""{"title":"x","status":409,"detail":"d","code":"{{code}}"}""", "application/problem+json");
        });
        var session = await harness.SignInAsync();
        var page = await session.GetAsync($"{Url(Open)}?plantilla=recordatorio_cita%7Ces");

        var answer = await session.SubmitAsync(FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "template-form", "template form"))
            .Set("Input.Values[0]", "Laura").Set("Input.Values[1]", "lunes"));

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains(words, answer.Document.QuerySelector("[data-testid=template-composer-error]")!.Plain());
        Assert.Equal("Laura", ((IHtmlInputElement)answer.Document.QuerySelector("#template-value-0")!).Value); // the values stay
    }

    [Fact]
    public async Task A_template_form_sent_twice_sends_once()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.WindowOpen = false);
        var session = await harness.SignInAsync();
        var page = await session.GetAsync($"{Url(Open)}?plantilla=recordatorio_cita%7Ces");
        var form = FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "template-form", "template form"))
            .Set("Input.Values[0]", "Laura").Set("Input.Values[1]", "lunes");

        await session.SubmitAsync(form);
        var second = await session.SubmitAsync(form);

        Assert.Single(harness.Stub.SentTemplates);
        Assert.Contains("ya se envió", second.Document.QuerySelector("[data-testid=template-composer-error]")!.Plain());
    }

    [Fact]
    public async Task Opening_a_conversation_never_marks_it_read_by_itself_the_script_posts_it_when_the_company_has_it_on()
    {
        using (var off = ConversationsHarness.Create())
        {
            var plain = await off.OpenAsync(Url(Open));
            Assert.Empty(Posts(off, "/read"));
            Assert.Null(plain.Document.QuerySelector("[data-testid=chat-card]")!.GetAttribute("data-read-url"));
            Assert.Null(plain.Document.QuerySelector("textarea[data-typing-url]"));
        }

        using var on = ConversationsHarness.Create(configure: stub => stub.ReadReceipts = true);
        var session = await on.SignInAsync();
        var page = await session.GetAsync(Url(Open));

        Assert.Empty(Posts(on, "/read")); // the render sends nothing (a prefetch or a reload would)
        var readUrl = page.Document.QuerySelector("[data-testid=chat-card]")!.GetAttribute("data-read-url")!;
        Assert.Equal($"/conversaciones/{Open}/leido", readUrl);
        Assert.Equal($"/conversaciones/{Open}/escribiendo", page.Document.QuerySelector("textarea[data-typing-url]")!.GetAttribute("data-typing-url"));

        var withoutToken = await session.PostRawAsync(readUrl, new FormUrlEncodedContent([]));
        Assert.NotEqual(HttpStatusCode.NoContent, withoutToken.StatusCode); // antiforgery
        var posted = await session.PostRawAsync(readUrl, new FormUrlEncodedContent([new("__RequestVerificationToken", page.AntiforgeryToken()!)]));
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
        Assert.Single(Posts(on, "/read"));
    }

    [Fact]
    public async Task The_chat_carries_what_the_script_polls_and_the_version_endpoint_answers_it()
    {
        using var harness = ConversationsHarness.Create();
        var session = await harness.SignInAsync();

        var page = await session.GetAsync(Url(Open));
        var card = page.Document.QuerySelector("[data-testid=chat-card]")!;
        Assert.Equal($"/conversaciones/{Open}/version", card.GetAttribute("data-version-url"));
        Assert.NotNull(card.GetAttribute("data-version"));

        var version = await session.GetAsync($"/conversaciones/{Open}/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        using var json = JsonDocument.Parse(version.Body);
        Assert.Equal(card.GetAttribute("data-version"), json.RootElement.GetProperty("version").GetString()); // unchanged: the script does nothing
    }

    [Fact]
    public async Task The_version_changes_when_the_window_closes_so_the_script_reloads_the_composer()
    {
        using var harness = ConversationsHarness.Create();
        var session = await harness.SignInAsync();
        var drawn = (await session.GetAsync(Url(Open))).Document.QuerySelector("[data-testid=chat-card]")!.GetAttribute("data-version");

        harness.Stub.WindowOpen = false;
        var version = await session.GetAsync($"/conversaciones/{Open}/version");

        using var json = JsonDocument.Parse(version.Body);
        Assert.NotEqual(drawn, json.RootElement.GetProperty("version").GetString());
    }

    // ---- The inbox ----------------------------------------------------------------------

    private static readonly DateTime Now = DateTime.UtcNow;

    private static InboxResponse Inbox() => new()
    {
        Items =
        [
            new() { Id = Guid.NewGuid(), ContactName = "Laura", ContactWaId = "573005550101", Status = "Open", AwaitingReply = true, LastInboundAt = Now.AddHours(-2), WindowOpen = true, ChangedAt = Now.AddHours(-2) },
            new() { Id = Guid.NewGuid(), ContactName = "Pedro", ContactWaId = "573005550102", Status = "Open", AwaitingReply = true, LastInboundAt = Now.AddHours(-23), WindowOpen = true, WindowClosingSoon = true, ChangedAt = Now.AddHours(-23) },
            new() { Id = Guid.NewGuid(), ContactName = "Ana", ContactWaId = "573005550103", Status = "Open", AwaitingReply = false, LastInboundAt = Now.AddDays(-2), WindowOpen = false, IsSandbox = true, ChangedAt = Now.AddDays(-2) },
        ],
    };

    /// <summary>The inbox version the stand-in API answers with; a test changes it to say "the inbox changed".</summary>
    private sealed class InboxApi
    {
        public string Version { get; set; } = "\"v1\"";

        public HttpResponseMessage Answer(BackendRequest request, InboxResponse response)
        {
            if (request.Header("If-None-Match") == Version)
            {
                var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
                unchanged.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(Version);
                return unchanged;
            }

            var answer = FakeBackend.Answer(HttpStatusCode.OK, response);
            answer.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(Version);
            return answer;
        }
    }

    // One answer per harness: the same rows on every poll, as an API whose inbox did not change.
    private static ConversationsHarness InboxHarness(InboxResponse? response = null, InboxApi? api = null)
    {
        var answer = response ?? Inbox();
        var inbox = api ?? new InboxApi();
        return ConversationsHarness.Create(configure: stub => stub.Overrides["GET /api/inbox"] = request => inbox.Answer(request, answer));
    }

    [Fact]
    public async Task The_inbox_lists_in_the_APIs_order_with_its_badges_and_a_list_the_script_refreshes()
    {
        using var harness = InboxHarness();

        var page = (await harness.OpenAsync("/bandeja?filtro=esperando")).ShouldBe(HttpStatusCode.OK);

        var rows = page.Document.QuerySelectorAll("[data-testid=inbox-item]").ToList();
        Assert.Equal(["true", "true", "false"], rows.Select(r => r.GetAttribute("data-awaiting")));
        Assert.Equal(["open", "closing", "closed"], rows.Select(r => r.QuerySelector("[data-testid=badge-window]")!.GetAttribute("data-window")));
        Assert.Contains("Sin responder", rows[0].Plain());
        Assert.NotNull(rows[2].QuerySelector("[data-testid=badge-sandbox]"));

        var list = page.Document.QuerySelector("[data-testid=inbox-list]")!;
        Assert.Equal("/bandeja/lista?filtro=esperando", list.GetAttribute("data-poll-url"));
        Assert.Equal("5", list.GetAttribute("data-poll-seconds"));
        Assert.NotNull(page.Document.QuerySelector("noscript")); // without JavaScript the page says how to refresh
        Assert.NotNull(page.Document.QuerySelector("[data-testid=inbox-refresh]"));

        var call = Assert.Single(harness.Backend.DataRequests, r => r.Path == "/api/inbox");
        Assert.Equal("?awaitingReply=true", call.Uri.Query);
    }

    [Fact]
    public async Task The_list_fragment_passes_its_ETag_to_the_API_and_answers_304_while_the_API_does_then_200_when_it_changed()
    {
        var api = new InboxApi();
        using var harness = InboxHarness(api: api);
        var session = await harness.SignInAsync();

        var first = await session.GetAsync("/bandeja/lista");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(3, first.Document.QuerySelectorAll("[data-testid=inbox-item]").Length);
        var etag = first.Header("ETag")!;
        Assert.Equal("\"v1\"", etag); // the API's version, as is

        var second = await session.GetAsync("/bandeja/lista", ("If-None-Match", etag));
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(string.Empty, second.Body);
        Assert.Equal(etag, harness.Backend.DataRequests.Last(r => r.Path == "/api/inbox").Header("If-None-Match"));

        api.Version = "\"v2\"";
        var third = await session.GetAsync("/bandeja/lista", ("If-None-Match", etag));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal("\"v2\"", third.Header("ETag"));
    }

    [Fact]
    public async Task The_polled_endpoints_are_rate_limited_per_user()
    {
        using var harness = InboxHarness();
        var session = await harness.SignInAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            statuses.Add((await session.GetAsync("/bandeja/lista")).StatusCode);
        }

        Assert.All(statuses.Take(60), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Mine_without_a_linked_advisor_says_why_the_inbox_is_empty()
    {
        using var harness = InboxHarness(new InboxResponse { NoAdvisorLinked = true });

        var page = await harness.OpenAsync("/bandeja?filtro=mias");

        Assert.NotNull(page.Document.QuerySelector("[data-testid=inbox-no-advisor]"));
    }

    [Fact]
    public async Task The_inbox_list_fragment_needs_a_session()
    {
        using var harness = InboxHarness();
        using var anonymous = harness.Host.NewSession();

        var response = await anonymous.GetAsync("/bandeja/lista");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(harness.Backend.DataRequests, r => r.Path == "/api/inbox");
    }
}
