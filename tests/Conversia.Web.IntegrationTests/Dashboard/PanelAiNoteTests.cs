using System.Net;
using AngleSharp.Dom;
using Conversia.Application.DTOs.Metrics;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Dashboard;

/// <summary>The panel's note on the AI: when it last updated the fields, in local time, without technical detail.</summary>
public class PanelAiNoteTests
{
    [Fact]
    public async Task With_the_ai_on_the_panel_says_when_the_ai_last_updated_its_fields_and_nothing_with_the_ai_off()
    {
        using var on = PanelHarness.Create(MetricsStub.Populated);
        on.Stub.AiAnalysis = new AiAnalysisStatusResponse { Enabled = true, LastUpdatedAt = new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc) };
        var page = (await on.OpenAsync()).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Análisis de IA: última actualización el 12 nov, 01:30", page.Document.QuerySelector("[data-testid=panel-ai-updated]")!.Plain());

        using var waiting = PanelHarness.Create(MetricsStub.Populated);
        waiting.Stub.AiAnalysis = new AiAnalysisStatusResponse { Enabled = true };
        Assert.Contains("Análisis de IA: aún no hay una actualización", (await waiting.OpenAsync()).Document.QuerySelector("[data-testid=panel-ai-updated]")!.Plain());

        using var off = PanelHarness.Create(MetricsStub.Populated);
        Assert.Null((await off.OpenAsync()).Document.QuerySelector("[data-testid=panel-ai-updated]"));
    }

    [Fact]
    public async Task When_the_ai_status_cannot_be_read_the_panel_still_shows_its_numbers()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        harness.Stub.Failures["/api/tenant/ai-analysis"] = () => FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, null);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        Assert.Null(page.Document.QuerySelector("[data-testid=panel-ai-updated]"));
        Assert.NotNull(page.Document.QuerySelector("[data-testid=panel-updated]"));
    }
}
