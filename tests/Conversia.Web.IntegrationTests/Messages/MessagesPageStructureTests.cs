using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Messages;

/// <summary>
/// The structure rules of the shared interface (one heading, landmarks, labelled controls, tables that scroll inside their own region so the
/// page itself never scrolls sideways at 375 px, no inline layout) applied to the messages pages in each of their states.
/// </summary>
public class MessagesPageStructureTests
{
    [Fact]
    public async Task The_messages_pages_follow_the_structure_rules_in_every_state()
    {
        var pages = new List<(string Label, PageResponse Page, bool Shell)>();

        using (var harness = MessagesHarness.Create())
        {
            foreach (var path in new[]
                     {
                         "/mensajes", $"/mensajes?linea={MessagesStub.LineCentro}&direccion=salientes", "/mensajes?auto=10", $"/mensajes?visto={MessagesStub.MsgReply}",
                         "/mensajes/eventos", $"/mensajes/eventos?linea={MessagesStub.LineNorte}&estado=Processed", "/mensajes/eventos?auto=5",
                     })
            {
                pages.Add((path, (await harness.OpenAsync(path)).ShouldBe(HttpStatusCode.OK), true));
            }
        }

        using (var empty = MessagesHarness.Create(configure: stub => (stub.Messages, stub.Deliveries) = ([], [])))
        {
            pages.Add(("/mensajes (empty)", (await empty.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.OK), true));
            pages.Add(("/mensajes/eventos (empty)", (await empty.OpenAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.OK), true));
        }

        using (var forbidden = MessagesHarness.Create([Permission.UsersView]))
        {
            pages.Add(("/mensajes (403)", (await forbidden.OpenAsync("/mensajes")).ShouldBe(HttpStatusCode.Forbidden), true));
            pages.Add(("/mensajes/eventos (403)", (await forbidden.OpenAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.Forbidden), true));
        }

        var problems = pages.SelectMany(p => PageStructureRules.Problems(p.Label, p.Page, p.Shell)).ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task The_messages_pages_have_no_template_wording()
    {
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"(?<![\p{L}\p{N}_])(?:plantillas?|templates?|multi-?espacios?|demos?|recorridos?|acme|skus?|productos?|DR\.Template)(?![\p{L}\p{N}_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        using var harness = MessagesHarness.Create();

        var hits = new List<string>();
        foreach (var path in new[] { "/mensajes", "/mensajes/eventos" })
        {
            var page = await harness.OpenAsync(path);
            hits.AddRange(SpanishUi.Extract(page).SelectMany(t => forbidden.Matches(t.Text).Select(m => $"{path}: «{m.Value}» in {t.Where}")));
        }

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }
}

/// <summary>The two pages against the real API and database: what the API sends is what the pages read.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class MessagesPagesWithTheRealApi(TestApiFactory api)
{
    [Fact]
    public async Task A_company_with_a_line_and_no_messages_sees_the_empty_pages_and_the_silent_line()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "peluquería", pending: true);
        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active");
        await ApiSeed.SetQuotaAsync(api, admin, owner.TenantId, 1);
        var line = (await ApiSeed.CreateLinesAsync(api, owner, "+57 300 888 0001"))[0];
        await ApiSeed.ActivateLineAsync(api, owner, line);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var messages = (await session.GetAsync("/mensajes")).ShouldBe(HttpStatusCode.OK);
        var deliveries = (await session.GetAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Aún no llegan mensajes", messages.Text);
        Assert.Equal("0", messages.Document.QuerySelector("[data-kpi=messages-today] [data-testid=kpi-value]")!.Plain().Trim());
        var status = Assert.Single(messages.Select("[data-testid=line-status]"));
        Assert.Equal("never", status.GetAttribute("data-delivery"));
        Assert.Contains("+57 300 888 0001", status.Plain());
        Assert.Contains("Aún no ha llegado ninguna entrega", deliveries.Text);
        Assert.Empty(PageStructureRules.Problems("/mensajes", messages, true));
        Assert.Empty(PageStructureRules.Problems("/mensajes/eventos", deliveries, true));
    }

    [Fact]
    public async Task A_pending_company_goes_to_its_page_and_a_member_without_the_permission_gets_the_403()
    {
        var pending = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: true);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "droguería");
        var member = await ApiSeed.CreateMemberAsync(api, owner, "sin-permisos", Permission.UsersView);
        using var host = WebTestHost.Create(api);

        using (var session = await WebFlows.SignedInAsync(host, pending.Email))
        {
            (await session.GetAsync("/mensajes")).ShouldRedirectTo("/cuenta-pendiente");
            (await session.GetAsync("/mensajes/eventos")).ShouldRedirectTo("/cuenta-pendiente");
        }

        using (var session = await WebFlows.SignedInAsync(host, member.Email))
        {
            (await session.GetAsync("/mensajes")).ShouldBe(HttpStatusCode.Forbidden);
            (await session.GetAsync("/mensajes/eventos")).ShouldBe(HttpStatusCode.Forbidden);
        }
    }
}
