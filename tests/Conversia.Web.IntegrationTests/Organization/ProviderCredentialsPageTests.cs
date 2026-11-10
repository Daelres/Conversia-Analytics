using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Organization;

/// <summary>
/// The provider API key of a WhatsApp number: what the list of /organizacion says about it, the page that sets or replaces it
/// (/organizacion/canales/{id}/clave-api) and the confirmation that removes it (/organizacion/canales/{id}/quitar-clave). The key is
/// written and never read back: no page, address, cookie or log line carries it once it has been submitted.
/// </summary>
public class ProviderCredentialsPageTests
{
    /// <summary>A dummy key in the shape the API accepts; the tests look for it everywhere it must not be.</summary>
    private const string Key = "d360-dummy-key-Qm7Zx2Lp9";

    private const string OtherKey = "d360-other-key-Hk4Vb8Nt1";

    private static string KeyUrl(Guid id) => $"/organizacion/canales/{id}/clave-api";

    private static string RemoveUrl(Guid id) => $"/organizacion/canales/{id}/quitar-clave";

    private static string ApiUrl(Guid id) => $"/api/channels/{id}/provider-credentials";

    private static IHtmlFormElement KeyForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "credentials-form", "credentials form");

    private static IHtmlFormElement ConfirmForm(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "channel-action-form", "channel action form");

    private static OrganizationHarness WithKeyedLine(IReadOnlyList<Permission>? member = null) => OrganizationHarness.Create(member, stub => stub.WithKeyedLine());

    private static AngleSharp.Dom.IElement Row(PageResponse page, Guid id) => page.Document.QuerySelector($"[data-testid=channel-row][data-channel-id='{id}']")!;

    private static JsonElement BodyOf(BackendRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    private static void AssertNowhere(string secret, WebSession session, OrganizationHarness harness)
    {
        Assert.All(session.Transcript, response => Assert.DoesNotContain(secret, response.Body));
        Assert.DoesNotContain(session.Transcript.SelectMany(r => r.SetCookies), cookie => cookie.Contains(secret));
        Assert.DoesNotContain(secret, string.Join("\n", session.Transcript.Select(r => r.RequestUri + " " + r.Header("Location"))));
        Assert.Empty(harness.Host.Logs.Mentioning(secret));
    }

    // ---- the list ----

    [Fact]
    public async Task The_list_says_for_each_number_whether_it_has_a_key_since_when_and_for_which_environment()
    {
        using var harness = WithKeyedLine();

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var without = Row(page, OrganizationStub.Line).QuerySelector("[data-testid=channel-key]")!;
        Assert.Equal("false", without.GetAttribute("data-has-key"));
        Assert.Contains("Sin clave", without.Plain());
        Assert.Contains("Sandbox (pruebas)", without.Plain());
        var with = Row(page, OrganizationStub.Keyed).QuerySelector("[data-testid=channel-key]")!;
        Assert.Equal("true", with.GetAttribute("data-has-key"));
        Assert.Equal("Production", with.GetAttribute("data-environment"));
        Assert.Contains("Configurada", with.Plain());
        Assert.Contains("Producción", with.Plain());
        Assert.Contains("1 oct 2026", with.Plain()); // the day it was set, at the time of the business
    }

    [Fact]
    public async Task A_viewer_reads_the_state_of_the_key_and_gets_no_way_to_change_it()
    {
        using var harness = WithKeyedLine([Permission.OrganizationView]);

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Configurada", Row(page, OrganizationStub.Keyed).QuerySelector("[data-testid=channel-key]")!.Plain());
        Assert.DoesNotContain(page.Select("a"), a => (a.GetAttribute("href") ?? string.Empty).Contains("clave", StringComparison.Ordinal));
        Assert.Empty(page.Select("input[type=password]"));
    }

    [Fact]
    public async Task A_manager_gets_one_link_per_number_to_its_key()
    {
        using var harness = WithKeyedLine();

        var page = await harness.OpenAsync("/organizacion");

        Assert.All(new[] { OrganizationStub.Line, OrganizationStub.Retired, OrganizationStub.Keyed }, id =>
        {
            var link = Assert.Single(Row(page, id).QuerySelectorAll("a"), a => a.GetAttribute("href") == KeyUrl(id));
            Assert.Contains("Clave de API", link.Plain());
        });
    }

    // ---- the page of the key ----

    [Fact]
    public async Task The_page_of_a_number_without_a_key_asks_for_one_in_a_field_that_is_never_filled_in()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync(KeyUrl(OrganizationStub.Line))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Clave de API del proveedor", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Contains("+57 300 000 0001", page.Document.QuerySelector("[data-testid=credentials-number]")!.Plain());
        Assert.Contains("Sin clave", page.Document.QuerySelector("[data-testid=credentials-state]")!.Plain());
        var input = Assert.Single(KeyForm(page).QuerySelectorAll("input[type=password]"));
        Assert.Equal("off", input.GetAttribute("autocomplete"));
        Assert.False(input.HasAttribute("value"));
        Assert.NotNull(page.Document.QuerySelector($"label[for='{input.Id}']"));
        var options = KeyForm(page).QuerySelectorAll("select option").ToList();
        Assert.Equal(["Sandbox", "Production"], options.Select(o => o.GetAttribute("value")!));
        Assert.Equal(["Sandbox (pruebas)", "Producción"], options.Select(o => o.Plain().Trim()));
        Assert.Equal("Sandbox", Assert.Single(options, o => o.HasAttribute("selected")).GetAttribute("value")); // the environment the line has now
        Assert.Empty(page.Select($"a[href='{RemoveUrl(OrganizationStub.Line)}']")); // nothing to remove
        Assert.Empty(harness.CredentialCalls);
    }

    [Fact]
    public async Task The_page_of_a_number_with_a_key_says_since_when_offers_to_replace_it_and_to_remove_it_and_never_shows_it()
    {
        using var harness = WithKeyedLine();

        var page = (await harness.OpenAsync(KeyUrl(OrganizationStub.Keyed))).ShouldBe(HttpStatusCode.OK);

        var state = page.Document.QuerySelector("[data-testid=credentials-state]")!.Plain();
        Assert.Contains("Configurada", state);
        Assert.Contains("1 oct 2026", state);
        Assert.False(Assert.Single(KeyForm(page).QuerySelectorAll("input[type=password]")).HasAttribute("value"));
        Assert.Contains("vacío", KeyForm(page).Plain()); // leaving it blank keeps the stored key
        Assert.Equal("Production", Assert.Single(KeyForm(page).QuerySelectorAll("select option"), o => o.HasAttribute("selected")).GetAttribute("value"));
        Assert.Contains("Quitar", Assert.Single(page.Select($"a[href='{RemoveUrl(OrganizationStub.Keyed)}']")).Plain());
    }

    [Fact]
    public async Task Saving_a_key_puts_it_to_the_api_and_lands_on_the_list_without_the_key_appearing_anywhere()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", Key).Set("Environment", "Sandbox"));

        var call = Assert.Single(harness.CredentialCalls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal(ApiUrl(OrganizationStub.Line), call.Path);
        Assert.Equal(Key, BodyOf(call).GetProperty("apiKey").GetString());
        Assert.Equal("Sandbox", BodyOf(call).GetProperty("environment").GetString());
        response.ShouldRedirectTo("/organizacion?notice=key-set");
        var list = await session.FollowAsync(response);
        Assert.Contains("Clave de API guardada", list.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.Contains("Configurada", Row(list, OrganizationStub.Line).QuerySelector("[data-testid=channel-key]")!.Plain());
        await session.GetAsync(KeyUrl(OrganizationStub.Line)); // and the page of the key does not show it afterwards either
        AssertNowhere(Key, session, harness);
    }

    [Fact]
    public async Task Replacing_a_key_sends_the_new_one_and_the_chosen_environment()
    {
        using var harness = WithKeyedLine();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Keyed));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", OtherKey).Set("Environment", "Sandbox"));

        var call = Assert.Single(harness.CredentialCalls);
        Assert.Equal(OtherKey, BodyOf(call).GetProperty("apiKey").GetString());
        Assert.Equal("Sandbox", BodyOf(call).GetProperty("environment").GetString());
        response.ShouldRedirectTo("/organizacion?notice=key-set");
        AssertNowhere(OtherKey, session, harness);
    }

    [Fact]
    public async Task Leaving_the_key_blank_on_a_number_that_has_one_changes_only_the_environment()
    {
        using var harness = WithKeyedLine();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Keyed));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("Environment", "Sandbox"));

        var body = BodyOf(Assert.Single(harness.CredentialCalls));
        Assert.True(!body.TryGetProperty("apiKey", out var key) || key.ValueKind == JsonValueKind.Null);
        Assert.Equal("Sandbox", body.GetProperty("environment").GetString());
        response.ShouldRedirectTo("/organizacion?notice=env-set");
        var list = await session.FollowAsync(response);
        Assert.Contains("Entorno", list.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.Contains("Sandbox (pruebas)", Row(list, OrganizationStub.Keyed).QuerySelector("[data-testid=channel-key]")!.Plain());
    }

    [Fact]
    public async Task Leaving_the_key_blank_on_a_number_without_one_is_refused_in_the_apis_words()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)));

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("La clave de API del proveedor es obligatoria", response.Text);
        Assert.NotNull(response.Document.QuerySelector("[data-testid=credentials-form]")); // the form is there to try again
    }

    [Theory]
    [InlineData("clave con espacios")]
    [InlineData("clave-con-ñ")]
    public async Task A_key_the_rules_refuse_never_reaches_the_api_and_is_not_written_back(string bad)
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", bad));

        Assert.Empty(harness.CredentialCalls);
        Assert.Equal("true", Assert.Single(KeyForm(response).QuerySelectorAll("input[type=password]")).GetAttribute("aria-invalid"));
        Assert.Contains("Clave de API del proveedor", response.Document.QuerySelector(".field__error")!.Plain());
        Assert.DoesNotContain(bad, response.Body);
        Assert.Empty(harness.Host.Logs.Mentioning(bad));
    }

    [Fact]
    public async Task An_environment_that_is_not_one_of_the_two_never_reaches_the_api()
    {
        using var harness = OrganizationHarness.Create();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", Key).Set("Environment", "https://evil.example.test"));

        Assert.Empty(harness.CredentialCalls);
        Assert.False(response.IsRedirect);
        Assert.DoesNotContain(Key, response.Body);
    }

    [Fact]
    public async Task A_refusal_of_the_api_is_shown_without_the_key()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides[$"PUT {ApiUrl(OrganizationStub.Line)}"] = _ => FakeBackend.Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"ApiKey":["El campo Clave de API del proveedor no es válido."]}}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", Key));

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("El campo Clave de API del proveedor no es válido.", response.Document.QuerySelector(".field__error")!.Plain());
        AssertNowhere(Key, session, harness);
    }

    [Fact]
    public async Task An_installation_without_its_master_key_says_who_can_fix_it()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides[$"PUT {ApiUrl(OrganizationStub.Line)}"] = _ => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable,
            """{"title":"x","status":503,"detail":"Las credenciales del proveedor no están disponibles en este momento.","code":"provider_credentials_unavailable"}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(KeyUrl(OrganizationStub.Line));

        var response = await session.SubmitAsync(FormModel.From(page, KeyForm(page)).Set("ApiKey", Key));

        response.ShouldBe(HttpStatusCode.ServiceUnavailable);
        Assert.Contains("credenciales del proveedor no están disponibles", response.Text);
        Assert.Contains("equipo de Conversia", response.Text);
        Assert.Empty(response.Select("[data-testid=error-code]")); // an expected state the API worded, not an unexpected error
        AssertNowhere(Key, session, harness);
    }

    [Fact]
    public async Task A_viewer_gets_a_403_on_the_page_of_the_key_and_a_forged_post_is_refused_by_the_api()
    {
        using var harness = OrganizationHarness.Create([Permission.OrganizationView]);
        harness.Stub.Overrides[$"PUT {ApiUrl(OrganizationStub.Line)}"] = _ => FakeBackend.Raw(HttpStatusCode.Forbidden, null);
        using var session = await harness.SignInAsync();

        var page = (await session.GetAsync(KeyUrl(OrganizationStub.Line))).ShouldBe(HttpStatusCode.Forbidden);
        Assert.Empty(page.Select("input[type=password]"));

        var forged = new FormModel(new Uri(WebTestHost.BaseAddress, KeyUrl(OrganizationStub.Line)), "Input.ApiKey", Key, (await WebFlows.AntiforgeryTokenAsync(session)))
            .Add("Input.Environment", "Sandbox").Add("_handler", "save-credentials");
        var response = await session.SubmitAsync(forged);

        response.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Single(harness.CredentialCalls); // it reached the API, which is the one that refuses
        AssertNowhere(Key, session, harness);
    }

    [Fact]
    public async Task An_unknown_number_is_a_404_and_an_anonymous_visitor_goes_to_the_login()
    {
        using var harness = OrganizationHarness.Create();
        using var anonymous = harness.Host.NewSession();

        var missing = (await harness.OpenAsync(KeyUrl(Guid.NewGuid()))).ShouldBe(HttpStatusCode.NotFound);

        Assert.Contains("Número no encontrado", missing.Text);
        Assert.Equal(KeyUrl(OrganizationStub.Line), (await anonymous.GetAsync(KeyUrl(OrganizationStub.Line))).ShouldRedirectToLogin());
    }

    // ---- removing the key ----

    [Fact]
    public async Task Removing_the_key_asks_first_and_says_what_stops_working()
    {
        using var harness = WithKeyedLine();

        var page = (await harness.OpenAsync(RemoveUrl(OrganizationStub.Keyed))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Quitar la clave de API", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Contains("¿Quitar la clave de API de este número?", page.Document.QuerySelector("[data-testid=channel-confirm]")!.Plain());
        Assert.Contains("deja de poder enviar mensajes", page.Document.QuerySelector("[data-testid=confirm-effect]")!.Plain());
        Assert.Contains("btn--danger", Assert.Single(ConfirmForm(page).QuerySelectorAll("button[type=submit]")).ClassName);
        Assert.Contains(page.Select("a"), a => a.Plain().Contains("Cancelar"));
        Assert.Empty(harness.CredentialCalls); // asking changes nothing
    }

    [Fact]
    public async Task Confirming_deletes_the_key_and_lands_on_the_list_with_a_confirmation()
    {
        using var harness = WithKeyedLine();
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(RemoveUrl(OrganizationStub.Keyed));

        var response = await session.SubmitAsync(FormModel.From(page, ConfirmForm(page)));

        var call = Assert.Single(harness.CredentialCalls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal(ApiUrl(OrganizationStub.Keyed), call.Path);
        response.ShouldRedirectTo("/organizacion?notice=key-removed");
        var list = await session.FollowAsync(response);
        Assert.Contains("Clave de API eliminada", list.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
        Assert.Contains("Sin clave", Row(list, OrganizationStub.Keyed).QuerySelector("[data-testid=channel-key]")!.Plain());
    }

    [Fact]
    public async Task A_viewer_cannot_open_the_removal()
    {
        using var harness = WithKeyedLine([Permission.OrganizationView]);

        (await harness.OpenAsync(RemoveUrl(OrganizationStub.Keyed))).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Empty(harness.CredentialCalls);
    }

    [Fact]
    public async Task The_pages_are_one_h1_each_and_all_their_text_is_spanish()
    {
        using var harness = WithKeyedLine();
        using var session = await harness.SignInAsync();
        var pages = new List<PageResponse>
        {
            await session.GetAsync("/organizacion"),
            await session.GetAsync(KeyUrl(OrganizationStub.Line)),
            await session.GetAsync(KeyUrl(OrganizationStub.Keyed)),
            await session.GetAsync(RemoveUrl(OrganizationStub.Keyed)),
        };
        var form = await session.GetAsync(KeyUrl(OrganizationStub.Line));
        pages.Add(await session.SubmitAsync(FormModel.From(form, KeyForm(form)).Set("ApiKey", "clave con espacios")));

        Assert.All(pages, page =>
        {
            Assert.Single(page.Select("main h1"));
            Assert.Empty(page.Select("[style]"));
            Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        });
    }
}
