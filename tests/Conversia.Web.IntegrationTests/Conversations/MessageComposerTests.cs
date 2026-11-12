using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.IntegrationTests.Organization;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>
/// The composer of /conversaciones/{id} against a stand-in API: when it is offered and why not, what a send does, and what a person
/// reads for every refusal of <c>POST /api/conversations/{id}/messages</c>.
/// </summary>
public class MessageComposerTests
{
    /// <summary>An open conversation of the stub.</summary>
    private static readonly Guid Open = ConversationsStub.Manual;

    private static string Url(Guid id) => $"/conversaciones/{id}";

    private static string SendPath(Guid id) => $"/api/conversations/{id}/messages";

    private static IHtmlFormElement ComposerForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "composer-form", "composer form");

    private static IElement Composer(PageResponse page) => page.Document.QuerySelector("[data-testid=composer]")!;

    private static IReadOnlyList<BackendRequest> Sends(ConversationsHarness harness) => harness.Backend.DataRequests.Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/messages", StringComparison.Ordinal)).ToList();

    private static string Problem(int status, string? code)
        => code is null ? $$"""{"title":"x","status":{{status}},"detail":"d"}""" : $$"""{"title":"x","status":{{status}},"detail":"Texto de la API.","code":"{{code}}"}""";

    private static async Task<(WebSession Session, PageResponse Response)> SendAsync(ConversationsHarness harness, string text, Guid? conversation = null)
    {
        var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(conversation ?? Open));
        return (session, await session.SubmitAsync(FormModel.From(page, ComposerForm(page)).Set("Text", text)));
    }

    // ---- offered ----

    [Fact]
    public async Task A_manager_of_an_open_conversation_gets_a_text_area_with_the_limit_a_counter_and_one_submit()
    {
        using var harness = ConversationsHarness.Create();

        var page = (await harness.OpenAsync(Url(Open))).ShouldBe(HttpStatusCode.OK);

        var composer = Composer(page);
        Assert.Equal("Available", composer.GetAttribute("data-state"));
        Assert.False(composer.HasAttribute("hidden"));
        var area = Assert.Single(composer.QuerySelectorAll("textarea"));
        Assert.Equal("4096", area.GetAttribute("maxlength"));
        Assert.Equal(string.Empty, ((IHtmlTextAreaElement)area).Value);
        Assert.NotNull(page.Document.QuerySelector($"label[for='{area.Id}']"));
        Assert.Equal("0 / 4096 caracteres", composer.QuerySelector("[data-testid=composer-count]")!.Plain().Trim());
        Assert.NotNull(composer.QuerySelector(area.GetAttribute("data-char-count")!)); // the script updates the number it points at
        Assert.True(ComposerForm(page).HasAttribute("data-submit-once")); // the script disables the button while the form is on its way
        Assert.Contains("Enviar mensaje", Assert.Single(ComposerForm(page).QuerySelectorAll("button[type=submit]")).Plain());
        Assert.False(ComposerForm(page).ClosestHidden());
        Assert.Null(composer.QuerySelector("[data-testid=composer-unavailable]"));
        Assert.Null(composer.QuerySelector("[data-testid=composer-sandbox-note]")); // a production line
        Assert.Empty(Sends(harness));
    }

    [Fact]
    public async Task A_sandbox_line_gets_a_note_about_who_receives_the_messages_and_the_25_requests()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Channel.ProviderEnvironment = "Sandbox");

        var page = await harness.OpenAsync(Url(Open));

        var note = Composer(page).QuerySelector("[data-testid=composer-sandbox-note]")!.Plain();
        Assert.Contains("Sandbox", note);
        Assert.Contains("solo llegan al número vinculado a la clave", note);
        Assert.Contains("25 solicitudes en total", note);
    }

    // ---- sending ----

    [Fact]
    public async Task Sending_posts_the_text_and_the_message_appears_in_the_thread_as_outbound()
    {
        using var harness = ConversationsHarness.Create();

        var (session, response) = await SendAsync(harness, "  Hola, te confirmo la cita del martes.  ");
        using var _ = session;

        var call = Assert.Single(Sends(harness));
        Assert.Equal(SendPath(Open), call.Path);
        Assert.Equal("Hola, te confirmo la cita del martes.", JsonDocument.Parse(call.Body!).RootElement.GetProperty("text").GetString());
        response.ShouldRedirectTo($"/conversaciones/{Open}?notice=sent");
        var page = (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Mensaje enviado", page.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        var last = page.Select("[data-testid=chat-message]").Last();
        Assert.Equal("Outbound", last.GetAttribute("data-direction"));
        Assert.Contains("chat__item--out", last.ClassName);
        Assert.Equal("Hola, te confirmo la cita del martes.", last.QuerySelector(".chat__text")!.Plain().Trim());
        Assert.Equal(string.Empty, ((IHtmlTextAreaElement)Composer(page).QuerySelector("textarea")!).Value); // ready for the next one
    }

    [Fact]
    public async Task Line_breaks_travel_as_the_counter_counted_them()
    {
        using var harness = ConversationsHarness.Create();

        var (session, _) = await SendAsync(harness, "Hola\r\n¿Te agendo?");
        using var __ = session;

        Assert.Equal("Hola\n¿Te agendo?", JsonDocument.Parse(Assert.Single(Sends(harness)).Body!).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_same_form_sent_twice_sends_one_message()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(Open));
        var form = FormModel.From(page, ComposerForm(page)).Set("Text", "Hola");

        var first = await session.SubmitAsync(form);
        var second = await session.SubmitAsync(form); // a double click, or a reload of the answer

        Assert.Single(Sends(harness));
        Assert.True(first.IsRedirect);
        Assert.False(second.IsRedirect);
        Assert.Contains("ya se envió", second.Document.QuerySelector("[data-testid=composer-error]")!.Plain());
    }

    [Fact]
    public async Task A_post_without_the_guard_of_the_form_sends_nothing()
    {
        using var harness = ConversationsHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(Open));

        var response = await session.SubmitAsync(FormModel.From(page, ComposerForm(page)).Set("Text", "Hola").Set("SubmissionId", "forged"));

        Assert.Empty(Sends(harness));
        Assert.False(response.IsRedirect);
    }

    // ---- what is typed ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_message_is_refused_on_the_page(string text)
    {
        using var harness = ConversationsHarness.Create();

        var (session, response) = await SendAsync(harness, text);
        using var _ = session;

        Assert.Empty(Sends(harness));
        Assert.False(response.IsRedirect);
        Assert.Equal("true", Composer(response).QuerySelector("textarea")!.GetAttribute("aria-invalid"));
        Assert.NotNull(Composer(response).QuerySelector(".field__error"));
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused_on_the_page_and_kept_with_its_count()
    {
        using var harness = ConversationsHarness.Create();
        var text = new string('a', 4097);

        var (session, response) = await SendAsync(harness, text);
        using var _ = session;

        Assert.Empty(Sends(harness));
        Assert.Equal(text, ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value);
        Assert.Equal("4097 / 4096 caracteres", Composer(response).QuerySelector("[data-testid=composer-count]")!.Plain().Trim());
        Assert.Contains("4096", Composer(response).QuerySelector(".field__error")!.Plain());
    }

    [Fact]
    public async Task A_message_of_exactly_the_limit_is_sent()
    {
        using var harness = ConversationsHarness.Create();

        var (session, response) = await SendAsync(harness, new string('a', 4096));
        using var _ = session;

        Assert.Single(Sends(harness));
        Assert.True(response.IsRedirect);
    }

    [Fact]
    public async Task A_validation_refusal_of_the_api_is_shown_and_the_text_is_kept()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"Text":["El texto del mensaje no es válido."]}}""");

        var (session, response) = await SendAsync(harness, "Hola");
        using var _ = session;

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("El texto del mensaje no es válido.", Composer(response).QuerySelector(".field__error")!.Plain());
        Assert.Equal("Hola", ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value);
    }

    // ---- every refusal ----

    [Theory]
    [InlineData(409, "conversation_closed", "cerrada")]
    [InlineData(409, "channel_inactive", "desactivado")]
    [InlineData(409, "provider_credentials_missing", "no tiene clave de API")]
    [InlineData(409, "provider_credentials_invalid", "no reconoce la clave de API")]
    [InlineData(422, "recipient_not_allowed", "no permite escribirle a este cliente")]
    [InlineData(422, "provider_rejected", "no aceptó el mensaje")]
    [InlineData(429, "provider_limit_reached", "25 solicitudes en total")]
    [InlineData(503, "provider_unavailable", "no se sabe si el mensaje salió")]
    [InlineData(503, "provider_credentials_unavailable", "credenciales del proveedor no están disponibles")]
    public async Task Each_refusal_of_the_api_reads_in_spanish_keeps_the_text_and_can_be_tried_again(int status, string code, string expected)
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw((HttpStatusCode)status, Problem(status, code));

        var (session, response) = await SendAsync(harness, "Hola, ¿sigues interesada?");
        using var _ = session;

        response.ShouldBe((HttpStatusCode)status);
        var error = Composer(response).QuerySelector("[data-testid=composer-error]")!;
        Assert.Contains(expected, error.Plain());
        Assert.Equal("alert", error.GetAttribute("role"));
        Assert.DoesNotContain(code, response.Text);
        Assert.DoesNotContain("Texto de la API.", response.Text); // the page words each refusal itself
        Assert.Equal("Hola, ¿sigues interesada?", ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value);
        Assert.Single(Sends(harness)); // one attempt: nothing retries by itself
        Assert.Empty(response.Select(ErrorCodeAssertions.Selector)); // an answer the API worded is not an unexpected error
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(response)));

        harness.Stub.Overrides.Remove($"POST {SendPath(Open)}");
        var retry = await session.SubmitAsync(FormModel.From(response, ComposerForm(response)));
        Assert.True(retry.IsRedirect); // the form drawn after a refusal carries a fresh guard
    }

    [Fact]
    public async Task An_unavailable_provider_says_to_check_the_phone_before_sending_again()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, Problem(503, "provider_unavailable"));

        var (session, response) = await SendAsync(harness, "Hola");
        using var _ = session;

        var error = Composer(response).QuerySelector("[data-testid=composer-error]")!.Plain();
        Assert.Contains("no se sabe si el mensaje salió", error);
        Assert.Contains("Verifica en el teléfono", error);
        Assert.Contains("antes de enviarlo de nuevo", error);
    }

    [Fact]
    public async Task A_message_that_left_but_was_not_recorded_says_not_to_send_it_again_and_empties_the_text()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.InternalServerError, Problem(500, "message_sent_not_recorded"));

        var (session, response) = await SendAsync(harness, "Hola, te espero mañana.");
        using var _ = session;

        response.ShouldBe(HttpStatusCode.InternalServerError);
        var error = Composer(response).QuerySelector("[data-testid=composer-error]")!.Plain();
        Assert.Contains("se envió", error);
        Assert.Contains("No lo envíes de nuevo", error);
        Assert.Equal(string.Empty, ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value); // nothing left to send by mistake
        Assert.Single(Sends(harness));
    }

    [Fact]
    public async Task The_apis_own_limit_says_how_long_to_wait()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.TooManyRequests, Problem(429, null), retryAfter: "37");

        var (session, response) = await SendAsync(harness, "Hola");
        using var _ = session;

        response.ShouldBe(HttpStatusCode.TooManyRequests);
        var error = Composer(response).QuerySelector("[data-testid=composer-error]")!.Plain();
        Assert.Contains("demasiados mensajes", error);
        Assert.Contains("37 segundos", error);
        Assert.DoesNotContain("25 solicitudes", error); // Conversia's own limit, not the provider's
        Assert.Equal("Hola", ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value);
    }

    [Fact]
    public async Task A_conversation_that_disappears_is_the_404_of_the_page()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.NotFound, null);

        var (session, response) = await SendAsync(harness, "Hola");
        using var _ = session;

        response.ShouldBe(HttpStatusCode.NotFound);
        Assert.Contains("no encontrada", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- not offered, and why ----

    private static void AssertNotOffered(PageResponse page, string state, string reason)
    {
        var composer = Composer(page);
        Assert.Equal(state, composer.GetAttribute("data-state"));
        Assert.Contains(reason, composer.QuerySelector("[data-testid=composer-unavailable]")!.Plain());
        Assert.Empty(composer.QuerySelectorAll("button[type=submit]"));
        Assert.All(composer.QuerySelectorAll("textarea"), area => Assert.True(area.ClosestHidden()));
        Assert.Null(composer.QuerySelector("[data-testid=composer-sandbox-note]"));
    }

    [Fact]
    public async Task A_closed_conversation_says_it_takes_no_more_messages()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Channel.ProviderEnvironment = "Sandbox");

        var page = (await harness.OpenAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);

        AssertNotOffered(page, "ConversationClosed", "está cerrada");
    }

    [Fact]
    public async Task An_inactive_line_says_so_and_where_to_switch_it_on()
    {
        using var harness = ConversationsHarness.Create(configure: stub => stub.Channel.IsActive = false);

        var page = await harness.OpenAsync(Url(Open));

        AssertNotOffered(page, "ChannelInactive", "desactivado");
        Assert.Contains("Organización", Composer(page).QuerySelector("[data-testid=composer-unavailable]")!.Plain());
    }

    [Fact]
    public async Task A_line_without_a_key_says_so_and_where_to_set_it()
    {
        using var harness = ConversationsHarness.Create(configure: stub => { stub.Channel.HasProviderApiKey = false; stub.Channel.ProviderEnvironment = "Sandbox"; });

        var page = await harness.OpenAsync(Url(Open));

        AssertNotOffered(page, "CredentialsMissing", "no tiene clave de API");
        Assert.Contains("Organización", Composer(page).QuerySelector("[data-testid=composer-unavailable]")!.Plain());
    }

    [Fact]
    public async Task A_viewer_is_told_which_permission_is_missing_and_a_forged_post_is_refused_by_the_api()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView, Permission.OrganizationView]);
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);
        using var session = await harness.SignInAsync();

        var page = (await session.GetAsync(Url(Open))).ShouldBe(HttpStatusCode.OK);
        AssertNotOffered(page, "NoPermission", "Gestionar conversaciones");

        var response = await session.SubmitAsync(FormModel.From(page, ComposerForm(page)).Set("Text", "Hola"));

        response.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Single(Sends(harness)); // it reached the API, which is the one that refuses
    }

    [Fact]
    public async Task A_forged_post_to_a_closed_conversation_is_refused_by_the_api_and_explained()
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(ConversationsStub.Won)}"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, Problem(409, "conversation_closed"));
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(ConversationsStub.Won));

        var response = await session.SubmitAsync(FormModel.From(page, ComposerForm(page)).Set("Text", "Hola"));

        response.ShouldBe(HttpStatusCode.Conflict);
        Assert.Contains("cerrada", Composer(response).Plain());
    }

    [Fact]
    public async Task A_manager_who_may_not_look_at_the_organization_gets_the_composer_and_the_sandbox_note()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView, Permission.ConversationsManage], stub => { stub.ChannelForbidden = true; stub.Channel.ProviderEnvironment = "Sandbox"; });

        var page = (await harness.OpenAsync(Url(Open))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Available", Composer(page).GetAttribute("data-state"));
        Assert.Single(ComposerForm(page).QuerySelectorAll("button[type=submit]"));
        Assert.Contains("25 solicitudes en total", Composer(page).QuerySelector("[data-testid=composer-sandbox-note]")!.Plain()); // the conversation itself says it
        Assert.Empty(page.Select(ErrorCodeAssertions.Selector));
    }

    [Theory]
    [InlineData(false, true, "ChannelInactive", "desactivado")]
    [InlineData(true, false, "CredentialsMissing", "no tiene clave de API")]
    public async Task A_manager_who_may_not_look_at_the_organization_is_told_why_the_line_cannot_send(bool active, bool key, string state, string reason)
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView, Permission.ConversationsManage],
            stub => { stub.ChannelForbidden = true; stub.Channel.IsActive = active; stub.Channel.HasProviderApiKey = key; });

        var page = (await harness.OpenAsync(Url(Open))).ShouldBe(HttpStatusCode.OK);

        AssertNotOffered(page, state, reason);
    }

    [Fact]
    public async Task The_page_never_asks_the_organization_for_the_line()
    {
        using var open = ConversationsHarness.Create();
        using var viewer = ConversationsHarness.Create([Permission.ConversationsView, Permission.OrganizationView]);

        await open.OpenAsync(Url(Open));
        await open.OpenAsync(Url(ConversationsStub.Won));
        await viewer.OpenAsync(Url(Open));

        Assert.DoesNotContain(open.Backend.DataRequests, r => r.Path.StartsWith("/api/channels", StringComparison.Ordinal));
        Assert.DoesNotContain(viewer.Backend.DataRequests, r => r.Path.StartsWith("/api/channels", StringComparison.Ordinal));
    }

    // ---- no usable answer: the outcome is unknown ----

    public static TheoryData<string> NoUsableAnswer => new()
    {
        "timeout", "connection dropped", "502", "504", "500 with an error id", "500 without a code", "503 with an unknown code", "422 with an unknown code",
    };

    private static HttpResponseMessage Unusable(string failure) => failure switch
    {
        "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout (simulated).", new TimeoutException()),
        "connection dropped" => throw new HttpRequestException("connection reset (simulated)"),
        "502" => FakeBackend.Raw(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html"),
        "504" => FakeBackend.Raw(HttpStatusCode.GatewayTimeout, null),
        "500 with an error id" => FakeBackend.UnexpectedWithCode(Conversia.Domain.Common.ErrorCode.New(DateTime.UtcNow)),
        "500 without a code" => FakeBackend.Raw(HttpStatusCode.InternalServerError, Problem(500, null)),
        "503 with an unknown code" => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, Problem(503, "database_unavailable")),
        "422 with an unknown code" => FakeBackend.Raw(HttpStatusCode.UnprocessableEntity, Problem(422, "something_new")),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    [Theory]
    [MemberData(nameof(NoUsableAnswer))]
    public async Task A_send_without_a_usable_answer_says_the_outcome_is_unknown_and_is_still_an_error_with_its_code(string failure)
    {
        using var harness = ConversationsHarness.Create();
        harness.Stub.Overrides[$"POST {SendPath(Open)}"] = _ => Unusable(failure);

        var (session, response) = await SendAsync(harness, "Hola, ¿sigues interesada?");
        using var _ = session;

        response.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = Composer(response).QuerySelector("[data-testid=composer-error]")!.Plain();
        Assert.Contains("no se sabe si el mensaje salió", error);
        Assert.Contains("Verifica en el teléfono", error);
        Assert.Contains("antes de enviarlo de nuevo", error);
        Assert.Equal("Hola, ¿sigues interesada?", ((IHtmlTextAreaElement)Composer(response).QuerySelector("textarea")!).Value);
        Assert.Single(Sends(harness)); // nothing retries by itself
        Assert.NotEmpty(response.Select(ErrorCodeAssertions.Selector)); // an unexpected failure: the person gets the code to quote
        if (failure != "500 with an error id")
        {
            Assert.NotEmpty(harness.Backend.ReportRequests); // and the Web reports what the API did not register itself
        }

        Assert.DoesNotContain("database_unavailable", response.Text);
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(response)));
    }

    [Fact]
    public async Task Every_state_of_the_composer_is_spanish_and_keeps_the_page_structure()
    {
        using var open = ConversationsHarness.Create(configure: stub => stub.Channel.ProviderEnvironment = "Sandbox");
        using var noKey = ConversationsHarness.Create(configure: stub => stub.Channel.HasProviderApiKey = false);
        using var viewer = ConversationsHarness.Create([Permission.ConversationsView]);
        var pages = new[]
        {
            await open.OpenAsync(Url(Open)), await open.OpenAsync(Url(ConversationsStub.Won)), await noKey.OpenAsync(Url(Open)), await viewer.OpenAsync(Url(Open)),
        };

        Assert.All(pages, page =>
        {
            Assert.Single(page.Select("main h1"));
            Assert.Single(page.Select("[data-testid=composer]"));
            Assert.Empty(page.Select("[style]"));
            Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        });
    }
}
