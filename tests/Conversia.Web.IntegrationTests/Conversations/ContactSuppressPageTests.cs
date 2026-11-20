extern alias ApiHost;

using System.Net;
using AngleSharp.Html.Dom;
using Conversia.Application.DTOs.Contacts;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>
/// The suppression of a customer's data from a conversation (Ley 1581; docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"): only with
/// the permission, only after the explicit confirmation, and always on the contact of the conversation, never one named by the form.
/// </summary>
public class ContactSuppressPageTests
{
    private static string Url(Guid id) => $"/conversaciones/{id}/suprimir-contacto";

    private static IHtmlFormElement Form(PageResponse page) => page.FindForm(f => f.GetAttribute("data-testid") == "suppress-form", "suppress-form");

    private static IReadOnlyList<BackendRequest> Suppressions(ConversationsHarness harness)
        => harness.Backend.DataRequests.Where(r => r.Method == HttpMethod.Post && r.Path.StartsWith("/api/contacts/", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task The_conversation_offers_the_suppression_only_to_who_may_do_it()
    {
        using var owner = ConversationsHarness.Create();
        using var viewer = ConversationsHarness.Create([Permission.ConversationsView, Permission.ConversationsManage]);

        var allowed = (await owner.OpenAsync($"/conversaciones/{ConversationsStub.Won}")).ShouldBe(HttpStatusCode.OK);
        var denied = (await viewer.OpenAsync($"/conversaciones/{ConversationsStub.Won}")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(Url(ConversationsStub.Won), allowed.Document.QuerySelector("[data-testid=suppress-contact-link]")!.GetAttribute("href"));
        Assert.Empty(denied.Select("[data-testid=suppress-contact-link]"));
    }

    [Fact]
    public async Task Without_the_permission_the_page_is_a_403_and_nothing_is_sent()
    {
        using var harness = ConversationsHarness.Create([Permission.ConversationsView]);

        var page = await harness.OpenAsync(Url(ConversationsStub.Won));

        page.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Contains("Suprimir datos de contactos", page.Text);
        Assert.Empty(Suppressions(harness));
    }

    [Fact]
    public async Task Suppressing_needs_the_confirmation_then_suppresses_the_conversations_contact_and_goes_to_the_list()
    {
        using var harness = ConversationsHarness.Create();
        var contactId = harness.Stub.All.First(c => c.Id == ConversationsStub.Won).Contact.Id;
        harness.Stub.Overrides[$"POST /api/contacts/{contactId}/suppression"] = _ =>
            FakeBackend.Answer(HttpStatusCode.OK, new ContactSuppressionResponse { ContactId = contactId, Conversations = 1, Messages = 3 });
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync(Url(ConversationsStub.Won))).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("No se puede deshacer", page.Document.QuerySelector("[data-testid=suppress-effect]")!.Plain());

        var unconfirmed = await session.SubmitAsync(FormModel.From(page, Form(page)));
        Assert.Contains("Confirma que el cliente pidió suprimir sus datos.", unconfirmed.Text);
        Assert.Empty(Suppressions(harness));

        var done = await session.SubmitAsync(FormModel.From(page, Form(page)).Add("Input.Confirmed", "true"));

        done.ShouldRedirectTo("/conversaciones?notice=contact-suppressed");
        Assert.Equal($"/api/contacts/{contactId}/suppression", Assert.Single(Suppressions(harness)).Path);
        var list = (await session.GetAsync("/conversaciones?notice=contact-suppressed")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Datos del cliente suprimidos", list.Document.QuerySelector("[data-testid=notice-success]")!.Plain());
    }
}
