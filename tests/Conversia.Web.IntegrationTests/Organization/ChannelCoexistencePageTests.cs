using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Organization;

/// <summary>
/// Coexistence in /organizacion (docs/MESSAGING.md, "Coexistence"): a line disconnected from the API is flagged with the reminder to open the app,
/// and /organizacion/canales/{id}/historial turns the history import on only for the owner, with the legal basis confirmed.
/// </summary>
public class ChannelCoexistencePageTests
{
    private static string HistoryUrl(Guid id) => $"/organizacion/canales/{id}/historial";

    private static AngleSharp.Dom.IElement Row(PageResponse page, Guid id) => page.Document.QuerySelector($"[data-testid=channel-row][data-channel-id='{id}']")!;

    private static OrganizationHarness Disconnected()
        => OrganizationHarness.Create(configure: stub => stub.Channels =
        [
            .. stub.Channels.Select(c => c.Id == OrganizationStub.Line ? Flag(c) : c),
        ]);

    private static ChannelResponse Flag(ChannelResponse channel)
    {
        channel.ProviderDisconnected = true;
        channel.ProviderDisconnectionEvent = "PARTNER_REMOVED";
        channel.ProviderDisconnectionReason = "PRIMARY_INACTIVITY";
        channel.ProviderDisconnectedAt = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        return channel;
    }

    [Fact]
    public async Task ADisconnectedLine_IsFlagged_WithTheReminderToOpenTheApp()
    {
        using var harness = Disconnected();

        var page = (await harness.OpenAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        var flag = Row(page, OrganizationStub.Line).QuerySelector("[data-testid=channel-coexistence]")!;
        Assert.Equal("true", flag.GetAttribute("data-disconnected"));
        Assert.Contains("Desconectada", flag.Plain());
        Assert.Contains("13 días", page.Text);
        Assert.Null(Row(page, OrganizationStub.Retired).QuerySelector("[data-testid=channel-coexistence][data-disconnected=true]"));
    }

    [Fact]
    public async Task TheHistoryPage_AsksTheOwnerToConfirmTheLegalBasis()
    {
        using var harness = OrganizationHarness.Create();

        var page = (await harness.OpenAsync(HistoryUrl(OrganizationStub.Line))).ShouldBe(HttpStatusCode.OK);

        var form = page.FindForm(f => f.GetAttribute("data-testid") == "history-form", "history form");
        var confirm = form.QuerySelector("input[type=checkbox][name='Input.LegalBasisConfirmed']")!;
        Assert.False(confirm.HasAttribute("checked"));
        Assert.Contains("base legal", page.Text);
        Assert.Contains("Ley 1581", page.Text);
        Assert.Contains("desactivada", page.Text);
    }

    [Fact]
    public async Task TurningItOn_SendsTheConfirmation_AndTheChoiceOfTheAi()
    {
        using var harness = OrganizationHarness.Create();
        harness.Stub.Overrides[$"PUT /api/channels/{OrganizationStub.Line}/history-import"] = request =>
        {
            var body = JsonDocument.Parse(request.Body!).RootElement;
            var channel = harness.Stub.Channels.First(c => c.Id == OrganizationStub.Line);
            channel.HistoryImportEnabled = body.GetProperty("enabled").GetBoolean();
            channel.HistoryAiAnalysisEnabled = body.GetProperty("analyzeWithAi").GetBoolean();
            return FakeBackend.Answer(HttpStatusCode.OK, channel);
        };
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync(HistoryUrl(OrganizationStub.Line));
        var form = FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "history-form", "history form"))
            .Add("Input.LegalBasisConfirmed", "true").Add("Input.AnalyzeWithAi", "true"); // the person ticks both boxes

        var answer = await session.SubmitAsync(form);

        Assert.True(answer.IsRedirect, $"{answer.Status} {answer.Text[..Math.Min(400, answer.Text.Length)]}");
        var call = Assert.Single(harness.Backend.DataRequests, r => r.Method == HttpMethod.Put && r.Path.EndsWith("/history-import", StringComparison.Ordinal));
        var sent = JsonDocument.Parse(call.Body!).RootElement;
        Assert.True(sent.GetProperty("enabled").GetBoolean());
        Assert.True(sent.GetProperty("legalBasisConfirmed").GetBoolean());
        Assert.True(sent.GetProperty("analyzeWithAi").GetBoolean());
        Assert.True(answer.IsRedirect, answer.Status.ToString());
    }

    [Fact]
    public async Task AMemberWhoIsNotTheOwner_IsToldOnlyTheOwnerCanChangeIt()
    {
        using var harness = OrganizationHarness.Create([Permission.OrganizationView, Permission.OrganizationManage]);

        var page = (await harness.OpenAsync(HistoryUrl(OrganizationStub.Line))).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Solo el propietario", page.Text);
        Assert.Null(page.Document.QuerySelector("[data-testid=history-form] button[type=submit]:not([hidden])"));
    }
}
