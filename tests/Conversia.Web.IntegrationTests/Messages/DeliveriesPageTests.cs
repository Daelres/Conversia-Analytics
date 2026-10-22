using System.Net;
using AngleSharp.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Messages;

/// <summary>GET /mensajes/eventos against a stand-in API: the deliveries with their outcome, the legend, the filters and every state.</summary>
public class DeliveriesPageTests
{
    private static IReadOnlyList<IElement> Rows(PageResponse page) => page.Select("[data-testid=delivery-row]").ToList();

    private static IElement Row(PageResponse page, string outcome) => Rows(page).Single(r => r.GetAttribute("data-outcome") == outcome);

    [Fact]
    public async Task Every_delivery_is_a_row_with_its_state_outcome_and_counts()
    {
        using var harness = MessagesHarness.Create();

        var page = (await harness.OpenAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(["stored", "receipts", "skipped", "failed", "pending"], Rows(page).Select(r => r.GetAttribute("data-outcome")));
        var stored = Row(page, "stored");
        Assert.Equal("Procesada", stored.QuerySelector("[data-testid=status-chip]")!.Plain().Trim());
        Assert.Equal("Mensajes guardados", stored.QuerySelector("[data-testid=outcome-chip]")!.Plain().Trim());
        Assert.Equal("1", stored.QuerySelector("[data-testid=delivery-stored]")!.Plain().Trim());
        Assert.Equal("+57 300 555 0200", stored.QuerySelector("[data-testid=delivery-line]")!.Plain().Trim());
        Assert.Equal("Solo recibos de entrega", Row(page, "receipts").QuerySelector("[data-testid=outcome-chip]")!.Plain().Trim());
        Assert.Equal("2", Row(page, "receipts").QuerySelector("[data-testid=delivery-receipts]")!.Plain().Trim());
        Assert.Equal("Mensajes omitidos", Row(page, "skipped").QuerySelector("[data-testid=outcome-chip]")!.Plain().Trim());
        Assert.Equal("1", Row(page, "skipped").QuerySelector("[data-testid=delivery-skipped]")!.Plain().Trim());
        Assert.Equal("Pendiente de procesar", Row(page, "pending").QuerySelector("[data-testid=outcome-chip]")!.Plain().Trim());
        Assert.Equal("—", Row(page, "pending").QuerySelector("[data-testid=delivery-stored]")!.Plain().Trim());
    }

    [Fact]
    public async Task A_failed_delivery_shows_its_attempts_and_its_error_as_text()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes/eventos");

        var failed = Row(page, "failed");
        Assert.Equal("Fallida", failed.QuerySelector("[data-testid=status-chip]")!.Plain().Trim());
        Assert.Equal("Falló", failed.QuerySelector("[data-testid=outcome-chip]")!.Plain().Trim());
        Assert.Contains("5", failed.Plain());
        Assert.Equal("InvalidOperationException: algo falló con [num] <b>marca</b>", failed.QuerySelector("[data-testid=delivery-error]")!.TextContent);
        Assert.Empty(failed.QuerySelectorAll("b"));
    }

    [Fact]
    public async Task The_page_says_the_content_is_never_shown_and_explains_every_outcome()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes/eventos");

        Assert.Contains("nunca se muestra el contenido", page.Document.QuerySelector("[data-testid=privacy-note]")!.Plain());
        var legend = page.Select("[data-testid=outcome-legend] .legend__row").Select(r => r.GetAttribute("data-outcome")).ToList();
        Assert.Equal(["stored", "partial", "receipts", "skipped", "repeated", "empty", "pending", "retrying", "failed"], legend);
        Assert.Contains("recibos", page.Document.QuerySelector("[data-testid=outcome-legend] [data-outcome=receipts]")!.Plain());
        Assert.DoesNotContain(MessagesStub.CustomerPhone, page.Body);
    }

    [Fact]
    public async Task The_filters_in_the_address_are_sent_to_the_api_and_shown_back_in_the_form()
    {
        using var harness = MessagesHarness.Create();

        var page = (await harness.OpenAsync($"/mensajes/eventos?linea={MessagesStub.LineCentro}&estado=Failed")).ShouldBe(HttpStatusCode.OK);

        var call = Assert.Single(harness.FeedCalls, c => c.Path == "/api/webhook-events");
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal(MessagesStub.LineCentro.ToString(), query["channelId"]);
        Assert.Equal("Failed", query["status"]);
        Assert.Equal("25", query["pageSize"]);
        var form = page.Document.QuerySelector("[data-testid=delivery-filters]")!;
        Assert.Equal("Failed", form.QuerySelector("select[name=estado] option[selected]")!.GetAttribute("value"));
        Assert.Equal(["failed"], Rows(page).Select(r => r.GetAttribute("data-outcome")));
        Assert.Equal(["", "Pending", "Processed", "Failed"], form.QuerySelectorAll("select[name=estado] option").Select(o => o.GetAttribute("value")!));
    }

    [Fact]
    public async Task A_hand_edited_address_with_nonsense_shows_the_unfiltered_page_and_refresh_works_like_the_feed()
    {
        using var harness = MessagesHarness.Create();

        var odd = (await harness.OpenAsync("/mensajes/eventos?linea=zzz&estado=Exploded&page=0&auto=3")).ShouldBe(HttpStatusCode.OK);
        var auto = await harness.OpenAsync("/mensajes/eventos?auto=5");

        Assert.Equal(5, Rows(odd).Count);
        Assert.Empty(odd.Select("meta[http-equiv=refresh]"));
        Assert.Equal("5", Assert.Single(auto.Select("meta[http-equiv=refresh]")).GetAttribute("content"));
        Assert.Equal("/mensajes/eventos?auto=10", auto.Document.QuerySelector("[data-testid=auto-choice][data-seconds='10']")!.GetAttribute("href"));
    }

    [Fact]
    public async Task A_long_list_is_paged()
    {
        using var harness = MessagesHarness.Create(configure: stub => stub.Total = 80);

        var page = await harness.OpenAsync("/mensajes/eventos?estado=Processed&page=2");

        var pager = page.Document.QuerySelector("[data-testid=pager]")!;
        Assert.Equal("/mensajes/eventos?estado=Processed", pager.QuerySelector("[data-testid=pager-prev]")!.GetAttribute("href"));
        Assert.Equal("/mensajes/eventos?estado=Processed&page=3", pager.QuerySelector("[data-testid=pager-next]")!.GetAttribute("href"));
        Assert.Contains("Página 2 de 4", pager.Plain());
    }

    [Fact]
    public async Task With_no_delivery_the_empty_state_says_the_notice_never_reached_the_webhook()
    {
        using var harness = MessagesHarness.Create(configure: stub => stub.Deliveries = []);

        var page = (await harness.OpenAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.OK);
        var filtered = await harness.OpenAsync("/mensajes/eventos?estado=Failed");

        Assert.Contains("Aún no ha llegado ninguna entrega", page.Text);
        Assert.Contains("el aviso no llegó al webhook", page.Text);
        Assert.Contains("Ninguna entrega coincide", filtered.Text);
    }

    [Fact]
    public async Task A_member_without_the_permission_gets_a_403_and_a_company_that_cannot_operate_the_pending_page()
    {
        using var forbidden = MessagesHarness.Create([Permission.UsersView]);
        using var pending = MessagesHarness.Create();
        pending.Stub.Overrides["GET /api/webhook-events"] = _ =>
            FakeBackend.Raw(HttpStatusCode.Forbidden, """{"status":403,"title":"Empresa no activa","detail":"Tu empresa está suspendida.","code":"tenant_not_active","tenantStatus":"Suspended"}""");

        var denied = (await forbidden.OpenAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.Forbidden);
        (await pending.OpenAsync("/mensajes/eventos")).ShouldRedirectTo("/cuenta-pendiente");

        Assert.Contains("Sin acceso a las entregas", denied.Text);
        Assert.Empty(denied.Select("[data-testid=delivery-row]"));
    }

    [Fact]
    public async Task All_the_text_of_the_page_is_spanish_and_the_shape_is_the_usual_one()
    {
        using var harness = MessagesHarness.Create();

        var page = await harness.OpenAsync("/mensajes/eventos");

        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page)));
        Assert.Single(page.Select("main h1"));
        Assert.Equal("deliveries", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
        Assert.Equal("page", page.Document.QuerySelector("nav a[href='/mensajes']")!.GetAttribute("aria-current"));
        var wrap = page.Document.QuerySelector("[data-testid=delivery-table]")!.Closest(".table-wrap")!;
        Assert.Equal("region", wrap.GetAttribute("role"));
        Assert.Equal("0", wrap.GetAttribute("tabindex"));
    }
}
