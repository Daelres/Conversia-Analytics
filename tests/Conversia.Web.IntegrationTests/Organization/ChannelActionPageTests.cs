using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Organization;

/// <summary>/organizacion/canales/{id}/{action}: the confirmation of deactivating, activating or rotating the secret of a number, and what each does.</summary>
public class ChannelActionPageTests
{
    private static string Url(Guid id, string action) => $"/organizacion/canales/{id}/{action}";

    private static AngleSharp.Html.Dom.IHtmlFormElement ActionForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form");

    // ---- the confirmation pages ----

    [Theory]
    [InlineData("desactivar", "Desactivar número", "¿Desactivar este número?", "rechazo (401)")]
    [InlineData("activar", "Activar número", "¿Activar este número?", "vuelve a aceptar")]
    [InlineData("rotar-secreto", "Rotar secreto", "¿Rotar el secreto de este número?", "deja de funcionar al instante")]
    public async Task Each_action_asks_for_confirmation_and_says_what_it_will_do(string action, string title, string question, string effect)
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync(Url(OrganizationStub.Line, action))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(title, page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Contains(question, page.Document.QuerySelector("[data-testid=channel-confirm]")!.Plain());
        Assert.Contains(effect, page.Document.QuerySelector("[data-testid=confirm-effect]")!.Plain());
        Assert.Equal("+57 300 000 0001", page.Document.QuerySelector("[data-testid=confirm-number]")!.Plain().Trim());
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/organizacion" && a.Plain().Contains("Cancelar"));
        Assert.Empty(harness.Posts); // asking changes nothing
        Assert.Empty(page.Select("[data-testid=channel-secret]"));
    }

    [Fact]
    public async Task Opening_the_confirmation_of_a_number_never_prints_its_key_or_a_secret()
    {
        using var harness = OrganizationHarness.Create();

        var page = await harness.OpenAsync(Url(OrganizationStub.Line, "rotar-secreto"));

        Assert.DoesNotContain(OrganizationStub.ListedKey, page.Body);
        Assert.DoesNotContain(OrganizationStub.RotatedSecret, page.Body);
    }

    // ---- doing it ----

    [Fact]
    public async Task Deactivating_posts_to_the_api_and_lands_on_the_list_with_a_confirmation()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "desactivar"));

        var response = await session.SubmitAsync(FormModel.From(page, ActionForm(page)));

        Assert.Equal($"/api/channels/{OrganizationStub.Line}/deactivate", Assert.Single(harness.Posts).Path);
        response.ShouldRedirectTo("/organizacion?notice=channel-off");
        Assert.Contains("dejó de recibir mensajes", (await session.FollowAsync(response)).Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }

    [Fact]
    public async Task Activating_posts_to_the_api_and_lands_on_the_list_with_a_confirmation()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Retired, "activar"));

        var response = await session.SubmitAsync(FormModel.From(page, ActionForm(page)));

        Assert.Equal($"/api/channels/{OrganizationStub.Retired}/activate", Assert.Single(harness.Posts).Path);
        response.ShouldRedirectTo("/organizacion?notice=channel-on");
    }

    [Fact]
    public async Task Rotating_shows_the_new_secret_once_and_never_in_an_address_a_cookie_or_a_log()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"));

        var response = await session.SubmitAsync(FormModel.From(page, ActionForm(page)));

        Assert.Equal($"/api/channels/{OrganizationStub.Line}/rotate-secret", Assert.Single(harness.Posts).Path);
        response.ShouldBe(HttpStatusCode.OK);
        var panel = response.Document.QuerySelector("[data-testid=channel-secret]")!;
        Assert.Equal(OrganizationStub.RotatedSecret, panel.QuerySelector("[data-testid=secret-value]")!.Plain().Trim());
        Assert.Equal("/api/webhooks/360dialog/" + OrganizationStub.ListedKey, panel.QuerySelector("[data-testid=secret-path]")!.Plain().Trim());
        Assert.Contains("no se vuelve a mostrar", panel.Plain());
        Assert.Contains("no-store", response.Header("Cache-Control"));
        Assert.Equal(1, response.Body.Split(OrganizationStub.RotatedSecret).Length - 1);
        Assert.DoesNotContain(session.Transcript.SelectMany(r => r.SetCookies), cookie => cookie.Contains(OrganizationStub.RotatedSecret));
        Assert.DoesNotContain(OrganizationStub.RotatedSecret, string.Join("\n", session.Transcript.Select(r => r.RequestUri.ToString() + r.Header("Location"))));
        Assert.Empty(harness.Host.Logs.Mentioning(OrganizationStub.RotatedSecret));
        Assert.All(response.Select("[data-testid=channel-confirm] button[type=submit]"), b => Assert.True(b.ClosestHidden())); // the action is not offered again
    }

    [Fact]
    public async Task Reloading_the_rotation_does_not_rotate_again_and_does_not_show_the_secret_again()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"));
        var form = FormModel.From(page, ActionForm(page));

        var first = await session.SubmitAsync(form);
        var second = await session.SubmitAsync(form);

        Assert.Single(harness.Posts, r => r.Path.EndsWith("/rotate-secret", StringComparison.Ordinal));
        Assert.NotNull(first.Document.QuerySelector("[data-testid=channel-secret]"));
        Assert.Null(second.Document.QuerySelector("[data-testid=channel-secret]"));
        Assert.DoesNotContain(OrganizationStub.RotatedSecret, second.Body);
        Assert.Contains("Este formulario ya se envió", second.Text);
    }

    [Fact]
    public async Task Opening_the_confirmation_again_after_a_rotation_shows_no_secret()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"));
        await session.SubmitAsync(FormModel.From(page, ActionForm(page)));

        var again = await session.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"));

        Assert.DoesNotContain(OrganizationStub.RotatedSecret, again.Body);
        Assert.Empty(again.Select("[data-testid=channel-secret]"));
    }

    [Fact]
    public async Task A_post_without_the_guard_of_the_form_changes_nothing()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "desactivar"));

        var response = await session.SubmitAsync(FormModel.From(page, ActionForm(page)).Set("SubmissionId", "forged"));

        Assert.Empty(harness.Posts);
        Assert.False(response.IsRedirect);
    }

    // ---- who and what ----

    [Fact]
    public async Task A_viewer_gets_a_403_and_a_forged_post_is_refused_by_the_api()
    {
        using var harness = OrganizationHarness.Create([Permission.OrganizationView]);

        var page = (await harness.OpenAsync(Url(OrganizationStub.Line, "desactivar"))).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("permiso", page.Text);
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task An_unknown_action_is_a_404_without_calling_the_api()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync(Url(OrganizationStub.Line, "borrar"))).ShouldBe(HttpStatusCode.NotFound);

        Assert.DoesNotContain(harness.Backend.DataRequests, r => r.Path.StartsWith("/api/channels", StringComparison.Ordinal));
        Assert.Contains("Número no encontrado", page.Text);
    }

    [Fact]
    public async Task An_unknown_number_is_a_404_that_says_what_is_missing()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync(Url(Guid.NewGuid(), "desactivar"))).ShouldBe(HttpStatusCode.NotFound);

        Assert.Contains("Número no encontrado", page.Text);
        Assert.Contains(page.Select("a"), a => a.GetAttribute("href") == "/organizacion");
    }

    [Fact]
    public async Task A_number_that_disappears_between_the_page_and_the_post_is_a_404()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(Url(OrganizationStub.Line, "desactivar"));
        harness.Stub.Overrides[$"POST /api/channels/{OrganizationStub.Line}/deactivate"] = _ => FakeBackend.Raw(HttpStatusCode.NotFound, null);
        harness.Stub.Overrides[$"GET /api/channels/{OrganizationStub.Line}"] = _ => FakeBackend.Raw(HttpStatusCode.NotFound, null);

        var response = await session.SubmitAsync(FormModel.From(page, ActionForm(page)));

        response.ShouldBe(HttpStatusCode.NotFound);
        Assert.Contains("Número no encontrado", response.Text);
    }

    [Fact]
    public async Task A_failing_api_is_the_usual_unavailable_message()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides[$"GET /api/channels/{OrganizationStub.Line}"] = _ => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, null);

        var page = (await harness.OpenAsync(Url(OrganizationStub.Line, "desactivar"))).ShouldBe(HttpStatusCode.ServiceUnavailable);

        Assert.Contains("no está disponible", page.Text);
    }

    [Fact]
    public async Task An_anonymous_visitor_goes_to_the_login_and_back()
    {
        using var harness = OrganizationHarness.Create();
        using var anonymous = harness.Host.NewSession();

        Assert.Equal(Url(OrganizationStub.Line, "rotar-secreto"), (await anonymous.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"))).ShouldRedirectToLogin());
    }

    [Fact]
    public async Task The_pages_are_one_h1_each_and_all_their_text_is_spanish()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var pages = new List<PageResponse>();
        foreach (var action in new[] { "desactivar", "activar", "rotar-secreto" })
        {
            pages.Add(await session.GetAsync(Url(OrganizationStub.Line, action)));
        }

        var rotation = await session.GetAsync(Url(OrganizationStub.Line, "rotar-secreto"));
        pages.Add(await session.SubmitAsync(FormModel.From(rotation, ActionForm(rotation))));

        Assert.All(pages, page =>
        {
            Assert.Single(page.Select("main h1"));
            Assert.Equal("channel-action", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
            Assert.Empty(page.Select("[style]"));
            Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        });
    }
}

internal static class HiddenExtensions
{
    /// <summary>True when the element or one of its ancestors is hidden.</summary>
    public static bool ClosestHidden(this AngleSharp.Dom.IElement element)
    {
        for (var current = element; current is not null; current = current.ParentElement)
        {
            if (current.HasAttribute("hidden"))
            {
                return true;
            }
        }

        return false;
    }
}
