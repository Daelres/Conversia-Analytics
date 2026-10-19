using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Organization;

/// <summary>GET and POST /organizacion against a stand-in API: the three lists, the three creation forms and the one-time webhook secret.</summary>
public class OrganizationPageTests
{
    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    private static JsonElement BodyOf(BackendRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    private static string EveryResponse(WebSession session) => string.Join("\n", session.Transcript.Select(r => r.Body + "\n" + string.Join("\n", r.Headers.Select(h => h.Key + ": " + h.Value)) + string.Join("\n", r.SetCookies)));

    // ---- the lists ----

    [Fact]
    public async Task The_three_lists_show_names_branches_and_states()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(["Centro", "Norte", "Sur (cerrada)"], page.Select("[data-testid=branch-row] th").Select(t => t.Plain().Trim()));
        Assert.Equal(["Activa", "Activa", "Inactiva"], page.Select("[data-testid=branch-row] .chip").Select(t => t.Plain().Trim()));
        var advisors = page.Select("[data-testid=advisor-row]").ToList();
        Assert.Equal(["Ana Torres", "Luis Pérez"], advisors.Select(a => a.QuerySelector("th")!.Plain().Trim()));
        Assert.Equal(["Centro", "Norte"], advisors.Select(a => a.QuerySelector("td[data-label=Sucursal]")!.Plain().Trim()));
        Assert.Equal(["Activo", "Inactivo"], advisors.Select(a => a.QuerySelector(".chip")!.Plain().Trim()));
        var channels = page.Select("[data-testid=channel-row]").ToList();
        Assert.Equal(["+57 300 000 0001", "+57 300 000 0002"], channels.Select(c => c.QuerySelector("th")!.Plain().Trim()));
        Assert.Equal(["true", "false"], channels.Select(c => c.GetAttribute("data-active")));
        Assert.Equal(["Activo", "Desactivado"], channels.Select(c => c.QuerySelector(".chip")!.Plain().Trim()));
    }

    [Fact]
    public async Task A_listed_webhook_key_is_never_printed_not_even_for_a_manager()
    {
        using var harness = OrganizationHarness.Create();

        var page = await harness.OpenAsync("/organizacion");

        Assert.DoesNotContain(OrganizationStub.ListedKey, page.Body);
        Assert.DoesNotContain("/api/webhooks/360dialog", page.Body);
        Assert.Empty(page.Select("[data-testid=channel-secret]"));
    }

    [Fact]
    public async Task A_manager_gets_the_forms_and_a_row_action_per_state_and_a_viewer_gets_none()
    {
        using var manager = OrganizationHarness.Create();
        using var viewer = OrganizationHarness.Create([Permission.OrganizationView]);

        var managed = (await manager.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
        var viewed = (await viewer.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(["/organizacion/canales/" + OrganizationStub.Line + "/desactivar", "/organizacion/canales/" + OrganizationStub.Line + "/rotar-secreto",
                      "/organizacion/canales/" + OrganizationStub.Retired + "/activar", "/organizacion/canales/" + OrganizationStub.Retired + "/rotar-secreto"],
            managed.Select("[data-testid=channel-row] td.table__cell-actions a").Select(a => a.GetAttribute("href")!));
        Assert.All(new[] { "branch-form-panel", "advisor-form-panel", "channel-form-panel" }, id => Assert.False(managed.Document.QuerySelector($"[data-testid={id}]")!.HasAttribute("hidden")));
        Assert.All(new[] { "branch-form-panel", "advisor-form-panel", "channel-form-panel" }, id => Assert.True(viewed.Document.QuerySelector($"[data-testid={id}]")!.HasAttribute("hidden")));
        Assert.DoesNotContain(viewed.Select("button[type=submit]"), b => b.Plain().Contains("Crear"));
        Assert.Empty(viewed.Select("td.table__cell-actions"));
        Assert.Equal(3, viewed.Select("[data-testid=branch-row]").Count());
    }

    [Fact]
    public async Task Empty_lists_say_what_to_do_next_for_a_manager_and_stay_quiet_for_a_viewer()
    {
        using var manager = OrganizationHarness.Create(configure: s => { s.Branches = []; s.Advisors = []; s.Channels = []; });
        using var viewer = OrganizationHarness.Create([Permission.OrganizationView], s => { s.Branches = []; s.Advisors = []; s.Channels = []; });

        var managed = await manager.OpenAsync("/organizacion");
        var viewed = await viewer.OpenAsync("/organizacion");

        Assert.Equal(3, managed.Select("[data-testid=empty-state]").Count());
        Assert.Contains("Crea la primera", managed.Text);
        Assert.Contains("Cuando alguien cree las sucursales", viewed.Text);
    }

    [Fact]
    public async Task A_member_without_the_organization_permission_gets_a_friendly_403()
    {
        using var harness = OrganizationHarness.Create([Permission.MetricsView]);
        harness.Stub.Overrides["GET /api/branches"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("Organización", page.Text);
        Assert.Empty(page.Select("[data-testid=branch-row]"));
    }

    [Fact]
    public async Task A_throttled_or_failing_api_gets_the_usual_friendly_messages()
    {
        using var throttled = OrganizationHarness.Create();
        throttled.Stub.Overrides["GET /api/channels"] = _ => FakeBackend.Raw(HttpStatusCode.TooManyRequests, """{"title":"x","status":429}""", retryAfter: "9");
        using var failing = OrganizationHarness.Create();
        failing.Stub.Overrides["GET /api/channels"] = _ => FakeBackend.Raw(HttpStatusCode.BadGateway, null);

        Assert.Contains("9 segundos", (await throttled.OpenAsync("/organizacion")).Text);
        Assert.Contains("no está disponible", (await failing.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.ServiceUnavailable).Text);
    }

    [Fact]
    public async Task An_anonymous_visitor_goes_to_the_login_and_back()
    {
        using var harness = OrganizationHarness.Create();
        using var anonymous = harness.Host.NewSession();

        Assert.Equal("/organizacion", (await anonymous.GetAsync("/organizacion")).ShouldRedirectToLogin());
    }

    // ---- branches and advisors ----

    [Fact]
    public async Task Creating_a_branch_posts_the_trimmed_name_and_lands_back_with_a_confirmation()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "branch-form")).Set("Name", "  Poblado  "));

        Assert.Equal("Poblado", BodyOf(Assert.Single(harness.Posts)).GetProperty("name").GetString());
        response.ShouldRedirectTo("/organizacion?notice=branch");
        Assert.Contains("Sucursal creada", (await session.FollowAsync(response)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }

    [Fact]
    public async Task A_duplicate_branch_name_is_a_message_on_the_field()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides["POST /api/branches"] = _ => FakeBackend.Raw(HttpStatusCode.Conflict, """{"title":"x","status":409}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "branch-form")).Set("Name", "Centro"));

        Assert.Contains("Ya existe una sucursal con ese nombre.", response.Text);
        Assert.Equal("true", response.Document.QuerySelector("#branch-name")!.GetAttribute("aria-invalid"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_branch_name_never_reaches_the_api(string name)
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "branch-form")).Set("Name", name));

        Assert.Empty(harness.Posts);
        Assert.Contains("obligatorio", response.Text);
    }

    [Fact]
    public async Task Creating_an_advisor_offers_only_active_branches_and_posts_the_branch_id()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");
        Assert.Equal(["Elige una sucursal", "Centro", "Norte"], page.Select("#advisor-branch option").Select(o => o.Plain().Trim()));

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "advisor-form")).Set("Name", "Marta Gil").Set("BranchId", OrganizationStub.Norte.ToString()));

        var body = BodyOf(Assert.Single(harness.Posts));
        Assert.Equal("Marta Gil", body.GetProperty("name").GetString());
        Assert.Equal(OrganizationStub.Norte, body.GetProperty("branchId").GetGuid());
        response.ShouldRedirectTo("/organizacion?notice=advisor");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-id")]
    public async Task An_advisor_without_a_valid_branch_never_reaches_the_api(string branch)
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "advisor-form")).Set("Name", "Marta").Set("BranchId", branch));

        Assert.Empty(harness.Posts);
        Assert.Contains("Elige la sucursal del asesor.", response.Text);
    }

    [Fact]
    public async Task The_apis_refusal_of_a_branch_is_shown_in_its_own_words()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides["POST /api/advisors"] = _ => FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"detail":"La sucursal indicada no existe."}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "advisor-form")).Set("Name", "Marta").Set("BranchId", OrganizationStub.Norte.ToString()));

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("La sucursal indicada no existe.", response.Text);
    }

    [Fact]
    public async Task A_forged_post_from_a_viewer_reaches_the_api_and_is_refused_there()
    {
        using var harness = OrganizationHarness.Create([Permission.OrganizationView]);
        harness.Stub.Overrides["POST /api/branches"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "branch-form")).Set("Name", "Hackeada"));

        response.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Single(harness.Posts);
    }

    // ---- creating a number: the one-time secret ----

    private static async Task<(WebSession Session, PageResponse Response)> CreateChannelAsync(OrganizationHarness harness, string phone = "+57 300 111 2222")
    {
        var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");
        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "channel-form")).Set("DisplayPhoneNumber", phone).Set("BranchId", OrganizationStub.Centro.ToString()));
        return (session, response);
    }

    [Fact]
    public async Task A_new_number_shows_its_path_and_secret_once_with_a_warning_and_copy_buttons()
    {
        using var harness = OrganizationHarness.Create();

        var (session, response) = await CreateChannelAsync(harness);
        using var _ = session;

        response.ShouldBe(HttpStatusCode.OK);
        var panel = response.Document.QuerySelector("[data-testid=channel-secret]")!;
        Assert.Equal("/api/webhooks/360dialog/" + OrganizationStub.NewKey, panel.QuerySelector("[data-testid=secret-path]")!.Plain().Trim());
        Assert.Equal(OrganizationStub.NewSecret, panel.QuerySelector("[data-testid=secret-value]")!.Plain().Trim());
        Assert.Contains("no se vuelve a mostrar", panel.QuerySelector("[data-testid=secret-warning]")!.Plain());
        Assert.Equal(["#secret-path", "#secret-value"], panel.QuerySelectorAll(".copy-btn").Select(b => b.GetAttribute("data-copy-target")!));
        Assert.All(panel.QuerySelectorAll(".copy-btn"), b => Assert.False(string.IsNullOrWhiteSpace(b.GetAttribute("aria-label"))));
        Assert.Contains("X-Webhook-Secret", panel.Plain());
        Assert.Contains("túnel", panel.QuerySelector("[data-testid=secret-howto]")!.Plain());
        Assert.Equal("/organizacion", panel.QuerySelector("[data-testid=secret-dismiss]")!.GetAttribute("href"));
        Assert.Equal(1, response.Body.Split(OrganizationStub.NewSecret).Length - 1); // exactly once in the answer
    }

    [Fact]
    public async Task The_secret_travels_in_no_address_no_cookie_and_no_cached_answer()
    {
        using var harness = OrganizationHarness.Create();

        var (session, response) = await CreateChannelAsync(harness);
        using var _ = session;

        Assert.False(response.IsRedirect);
        Assert.DoesNotContain(OrganizationStub.NewSecret, string.Join("\n", session.Transcript.Select(r => r.RequestUri.ToString() + r.Header("Location"))));
        Assert.DoesNotContain(session.Transcript.SelectMany(r => r.SetCookies), cookie => cookie.Contains(OrganizationStub.NewSecret) || cookie.Contains(OrganizationStub.NewKey));
        Assert.Contains("no-store", response.Header("Cache-Control"));
        Assert.DoesNotContain(OrganizationStub.NewSecret, string.Join("\n", session.AuthCookies.Select(c => c.Value)));
    }

    [Fact]
    public async Task The_secret_is_never_logged()
    {
        using var harness = OrganizationHarness.Create();

        var (session, _) = await CreateChannelAsync(harness);
        using var __ = session;

        Assert.Empty(harness.Host.Logs.Mentioning(OrganizationStub.NewSecret));
        Assert.Empty(harness.Host.Logs.Mentioning(OrganizationStub.NewKey));
    }

    [Fact]
    public async Task Opening_the_page_again_never_shows_the_secret_or_the_path()
    {
        using var harness = OrganizationHarness.Create();
        var (session, _) = await CreateChannelAsync(harness);
        using var __ = session;

        var again = await session.GetAsync("/organizacion");

        Assert.Empty(again.Select("[data-testid=channel-secret]"));
        Assert.DoesNotContain(OrganizationStub.NewSecret, again.Body);
        Assert.DoesNotContain(OrganizationStub.NewKey, again.Body);
    }

    [Fact]
    public async Task Sending_the_same_form_twice_creates_one_number_and_shows_the_secret_once()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");
        var form = FormModel.From(page, Form(page, "channel-form")).Set("DisplayPhoneNumber", "+57 300 111 2222").Set("BranchId", OrganizationStub.Centro.ToString());

        var first = await session.SubmitAsync(form);
        var second = await session.SubmitAsync(form);

        Assert.Single(harness.Posts, r => r.Path == "/api/channels");
        Assert.NotNull(first.Document.QuerySelector("[data-testid=channel-secret]"));
        Assert.Null(second.Document.QuerySelector("[data-testid=channel-secret]"));
        Assert.DoesNotContain(OrganizationStub.NewSecret, second.Body);
        Assert.Contains("Este formulario ya se envió", second.Text);
    }

    [Fact]
    public async Task A_post_without_the_guard_of_the_form_creates_nothing()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var response = await session.SubmitAsync(FormModel.From(page, Form(page, "channel-form")).Set("DisplayPhoneNumber", "+57 300 111 2222").Set("BranchId", OrganizationStub.Centro.ToString()).Set("SubmissionId", "forged"));

        Assert.Empty(harness.Posts);
        Assert.Null(response.Document.QuerySelector("[data-testid=channel-secret]"));
    }

    [Fact]
    public async Task A_refused_number_can_be_corrected_and_sent_again()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides["POST /api/channels"] = _ => FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"DisplayPhoneNumber":["El número no es válido."]}}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");

        var refused = await session.SubmitAsync(FormModel.From(page, Form(page, "channel-form")).Set("DisplayPhoneNumber", "+57 300 111 2222").Set("BranchId", OrganizationStub.Centro.ToString()));

        refused.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("El número no es válido.", refused.Text);
        harness.Stub.Overrides.Remove("POST /api/channels");
        var again = await session.SubmitAsync(FormModel.From(refused, Form(refused, "channel-form")).Set("DisplayPhoneNumber", "+57 300 111 2222").Set("BranchId", OrganizationStub.Centro.ToString()));
        Assert.NotNull(again.Document.QuerySelector("[data-testid=channel-secret]"));
    }

    [Theory]
    [InlineData("", "c")]
    [InlineData("abc", "c")]
    [InlineData("+57 300 111 2222", "")]
    [InlineData("+57 300 111 2222", "not-a-guid")]
    public async Task A_number_without_a_phone_or_a_branch_never_reaches_the_api(string phone, string branch)
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync("/organizacion");
        var branchId = branch == "c" ? OrganizationStub.Centro.ToString() : branch;

        await session.SubmitAsync(FormModel.From(page, Form(page, "channel-form")).Set("DisplayPhoneNumber", phone).Set("BranchId", branchId));

        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task An_empty_provider_id_is_not_sent()
    {
        using var harness = OrganizationHarness.Create();

        var (session, _) = await CreateChannelAsync(harness);
        using var __ = session;

        var body = BodyOf(Assert.Single(harness.Posts));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("providerPhoneNumberId").ValueKind);
        Assert.Equal("+57 300 111 2222", body.GetProperty("displayPhoneNumber").GetString());
    }

    // ---- shape ----

    [Fact]
    public async Task The_page_is_one_h1_in_the_shell_with_its_entry_active_and_all_its_text_in_spanish()
    {
        using var harness = OrganizationHarness.Create();
        var (session, response) = await CreateChannelAsync(harness);
        using var _ = session;
        var listed = await session.GetAsync("/organizacion");

        foreach (var page in new[] { response, listed })
        {
            Assert.Single(page.Select("main h1"));
            Assert.Equal("organization", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
            Assert.Equal("page", page.Document.QuerySelector("nav a[href='/organizacion']")!.GetAttribute("aria-current"));
            Assert.Empty(page.Select("[style]"));
            Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
            Assert.NotNull(page.FindLogoutForm());
        }
    }
}
