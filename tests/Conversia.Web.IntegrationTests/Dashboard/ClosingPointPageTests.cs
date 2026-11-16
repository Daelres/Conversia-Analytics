using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Dashboard;

/// <summary>
/// GET /conversaciones/hasta-donde-llegan: "Hasta dónde llegan las conversaciones" (docs/METRICS.md, "Closing point"). The panel already has
/// its six indicators, so this view lives in the conversations area.
/// </summary>
public class ClosingPointPageTests
{
    private const string Path = "/conversaciones/hasta-donde-llegan";

    private static async Task<PageResponse> OpenAsync(PanelHarness harness, string query = "")
    {
        using var session = await harness.SignInAsync();
        return await session.GetAsync(Path + query);
    }

    [Fact]
    public async Task It_shows_where_the_conversations_without_a_sale_stopped_and_the_main_drop_off()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await OpenAsync(harness)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Se compartió el precio", page.Document.QuerySelector("[data-testid=main-drop-off]")!.Plain().Trim());
        var stages = page.Select("[data-testid=stage-row]").ToList();
        Assert.Equal(["FirstContact", "NeedsIdentified", "PriceShared", "ObjectionRaised", "AppointmentProposed", "AppointmentBooked", "PaymentOrDeposit"],
            stages.Select(r => r.GetAttribute("data-stage")));
        Assert.Contains("14", stages[2].Plain());
        Assert.Contains("38,9 %", stages[2].Plain());
        Assert.Contains("40 conversaciones terminaron sin venta", page.Document.QuerySelector("[data-testid=stopped]")!.Plain());
        Assert.Contains("4 aún sin leer", page.Document.QuerySelector("[data-testid=stopped]")!.Plain());
        Assert.Contains("15,0 %", page.Document.QuerySelector("[data-testid=moved-off]")!.Plain());
        Assert.Contains("Llamada telefónica", page.Document.QuerySelector("[data-testid=moved-off]")!.Plain());
    }

    [Fact]
    public async Task The_filters_of_the_other_metrics_reach_the_api()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        await OpenAsync(harness, $"?period=7d&branch={MetricsStub.Centro}&advisor={MetricsStub.Ana}&source=Ads");

        var call = Assert.Single(harness.Stub.MetricCalls, u => u.AbsolutePath == "/api/metrics/closing-point");
        Assert.Contains($"branchId={MetricsStub.Centro}", call.Query);
        Assert.Contains($"advisorId={MetricsStub.Ana}", call.Query);
        Assert.Contains("source=Ads", call.Query);
    }

    [Fact]
    public async Task An_odd_source_is_ignored_not_sent()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        await OpenAsync(harness, "?source=Robots");

        Assert.DoesNotContain("source=", Assert.Single(harness.Stub.MetricCalls, u => u.AbsolutePath == "/api/metrics/closing-point").Query);
    }

    [Fact]
    public async Task Without_conversations_without_a_sale_it_says_so()
    {
        using var harness = PanelHarness.Create(currentTo =>
        {
            var stub = MetricsStub.Populated(currentTo);
            stub.ClosingPoint = stub.ClosingPoint with { Stopped = 0, NotRead = 0, MainDropOff = null, Stages = [.. stub.ClosingPoint.Stages.Select(s => s with { Count = 0, SharePercent = null })] };
            return stub;
        });

        var page = await OpenAsync(harness);

        Assert.NotNull(page.Document.QuerySelector("[data-testid=closing-point-empty]"));
        Assert.Null(page.Document.QuerySelector("[data-testid=main-drop-off]"));
    }

    [Fact]
    public async Task A_member_without_the_metrics_permission_is_refused()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated, member: [Permission.ConversationsView]);

        var page = await OpenAsync(harness);

        Assert.Equal("forbidden", page.Document.QuerySelector("[data-page]")!.GetAttribute("data-page"));
        Assert.DoesNotContain(harness.Stub.MetricCalls, u => u.AbsolutePath == "/api/metrics/closing-point");
    }

    [Fact]
    public async Task The_conversations_list_links_to_it()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        using var session = await harness.SignInAsync();

        var page = await session.GetAsync("/conversaciones");

        Assert.NotNull(page.Document.QuerySelector($"a[href='{Path}']"));
    }
}
