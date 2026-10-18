using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Products;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The conversation read API, the advisor assignment and the manual outcome, on a real PostgreSQL.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class ConversationsApiTests
{
    private const string Url = "/api/conversations";

    // Fixed past dates: the read API does not depend on the current time.
    private static readonly DateTime D0 = new(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConversationsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<ChannelFixture> NewTenantAsync() => StartChannelAsync(_factory, _client);

    private Task<Seeded> SeedAsync(ChannelFixture f, DateTime startedAt, params Msg[] messages)
        => SeedConversationAsync(_factory.Services, f, startedAt, messages.Length == 0 ? [In(0)] : messages);

    private static async Task<PagedResponse<ConversationListItem>> ListOkAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(query.Length == 0 ? Url : $"{Url}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<ConversationListItem>>())!;
    }

    // ---- List: order, paging and filters ------------------------------------------------------

    [Fact]
    public async Task TheList_IsNewestFirst_AndPaged()
    {
        using var f = await NewTenantAsync();
        var old = await SeedAsync(f, D0);
        var middle = await SeedAsync(f, D0.AddHours(1));
        var newest = await SeedAsync(f, D0.AddHours(2));

        var first = await ListOkAsync(f.Tenant.Owner.Client, "pageSize=2");
        var second = await ListOkAsync(f.Tenant.Owner.Client, "pageSize=2&page=2");
        var beyond = await ListOkAsync(f.Tenant.Owner.Client, "pageSize=2&page=3");

        Assert.Equal([newest.ConversationId, middle.ConversationId], first.Items.Select(i => i.Id));
        Assert.Equal([old.ConversationId], second.Items.Select(i => i.Id));
        Assert.Empty(beyond.Items);
        Assert.All(new[] { first, second, beyond }, p => Assert.Equal(3, p.TotalCount));
        Assert.Equal((1, 2), (first.Page, first.PageSize));
    }

    [Fact]
    public async Task TheListItem_CarriesTheContactTheBranchAndTheOpportunity()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0, In(0, "Hola"), Out(60), In(120, "Gracias"));

        var item = Assert.Single((await ListOkAsync(f.Tenant.Owner.Client)).Items);

        Assert.Equal(seeded.ConversationId, item.Id);
        Assert.Equal((seeded.ContactId, "Cliente"), (item.Contact.Id, item.Contact.DisplayName));
        Assert.Matches("^[0-9]{7,15}$", item.Contact.WaId);
        Assert.Equal((f.Branch.Id, f.Branch.Name, f.Channel.Id), (item.BranchId, item.BranchName, item.ChannelId));
        Assert.Equal(("Organic", "Open", 3), (item.Source, item.Status, item.MessageCount));
        Assert.Equal((D0, D0.AddSeconds(120), D0, D0.AddSeconds(60)), (item.StartedAt, item.LastMessageAt, item.FirstInboundAt, item.FirstResponseAt));
        Assert.False(item.Unanswered);
        Assert.Equal(("New", "Auto", "None"), (item.Opportunity.Stage, item.Opportunity.StageSource, item.Opportunity.Objection));
        Assert.Null(item.AdvisorId);
    }

    [Fact]
    public async Task TheListShowsNoMessageText()
    {
        using var f = await NewTenantAsync();
        await SeedAsync(f, D0, In(0, "texto-privado-del-cliente"));

        var response = await f.Tenant.Owner.Client.GetAsync(Url);

        Assert.DoesNotContain("texto-privado-del-cliente", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EnumsAreWrittenByName_NotByNumber()
    {
        using var f = await NewTenantAsync();
        await SeedAsync(f, D0);

        var body = await (await f.Tenant.Owner.Client.GetAsync(Url)).Content.ReadAsStringAsync();

        Assert.Contains("\"stage\":\"New\"", body);
        Assert.Contains("\"source\":\"Organic\"", body);
        Assert.Contains("\"status\":\"Open\"", body);
    }

    [Fact]
    public async Task FilterByBranch_Advisor_AndUnassigned()
    {
        using var f = await NewTenantAsync();
        var other = await CreateBranchAsync(f.Tenant.Owner.Client);
        var otherChannel = await CreateChannelAsync(f.Tenant.Owner.Client, other.Id, "+57 300 555 0111", "pnid-2");
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);
        var assigned = await SeedConversationAsync(_factory.Services, f, D0, [In(0)], advisorId: advisor.Id);
        var unassigned = await SeedConversationAsync(_factory.Services, f, D0.AddHours(1), [In(0)]);
        var elsewhere = await SeedConversationAsync(_factory.Services, f.TenantId, other.Id, otherChannel.Id, D0.AddHours(2), [In(0)]);
        var client = f.Tenant.Owner.Client;

        Assert.Equal([elsewhere.ConversationId], (await ListOkAsync(client, $"branchId={other.Id}")).Items.Select(i => i.Id));
        Assert.Equal([assigned.ConversationId], (await ListOkAsync(client, $"advisorId={advisor.Id}")).Items.Select(i => i.Id));
        Assert.Equal([elsewhere.ConversationId, unassigned.ConversationId], (await ListOkAsync(client, "unassigned=true")).Items.Select(i => i.Id));
        Assert.Equal(advisor.Name, (await ListOkAsync(client, $"advisorId={advisor.Id}")).Items.Single().AdvisorName);
    }

    [Fact]
    public async Task FilterByStageSourceStatusAndUnanswered()
    {
        using var f = await NewTenantAsync();
        var won = await SeedConversationAsync(_factory.Services, f, D0, [In(0), Out(30)], configureOpportunity: o => o.SetManualOutcome(OpportunityStage.Won, null, null, D0));
        var ads = await SeedConversationAsync(_factory.Services, f, D0.AddHours(1), [In(0), Out(30)], source: ConversationSource.Ads);
        var closed = await SeedConversationAsync(_factory.Services, f, D0.AddHours(2), [In(0), Out(30)], closed: true);
        var silent = await SeedConversationAsync(_factory.Services, f, D0.AddHours(3), [In(0)]);
        var advisorOnly = await SeedConversationAsync(_factory.Services, f, D0.AddHours(4), [Out(0)]); // the business wrote, nobody answered: not "unanswered"
        var client = f.Tenant.Owner.Client;

        Assert.Equal([won.ConversationId], (await ListOkAsync(client, "stage=Won")).Items.Select(i => i.Id));
        Assert.Equal([ads.ConversationId], (await ListOkAsync(client, "source=ads")).Items.Select(i => i.Id));
        Assert.Equal([closed.ConversationId], (await ListOkAsync(client, "status=Closed")).Items.Select(i => i.Id));
        Assert.Equal([silent.ConversationId], (await ListOkAsync(client, "unanswered=true")).Items.Select(i => i.Id));
        Assert.Equal(5, (await ListOkAsync(client, "unanswered=false")).TotalCount);
        Assert.Contains(advisorOnly.ConversationId, (await ListOkAsync(client)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task TheDateRange_UsesTheTenantTimeZone_FromInclusive_ToExclusive()
    {
        using var f = await NewTenantAsync();
        // Bogotá is UTC-5: 2026-10-19 04:30Z is still 18 October there, 05:00Z is already the 19th.
        var lateOnThe18th = await SeedAsync(f, new DateTime(2026, 10, 19, 4, 30, 0, DateTimeKind.Utc));
        var midnightOfThe19th = await SeedAsync(f, new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc));
        var earlyOnThe18th = await SeedAsync(f, new DateTime(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc));
        var client = f.Tenant.Owner.Client;

        Assert.Equal(
            [lateOnThe18th.ConversationId, earlyOnThe18th.ConversationId],
            (await ListOkAsync(client, "from=2026-10-18&to=2026-10-19")).Items.Select(i => i.Id));
        Assert.Equal([midnightOfThe19th.ConversationId], (await ListOkAsync(client, "from=2026-10-19")).Items.Select(i => i.Id));
        Assert.Empty((await ListOkAsync(client, "to=2026-10-18")).Items); // exclusive: nothing started before the 18th began
    }

    // ---- List: validation ---------------------------------------------------------------------

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("stage=7")]
    [InlineData("stage=Almost")]
    [InlineData("source=1,2")]
    [InlineData("status=Archived")]
    [InlineData("from=2026-10-19&to=2026-10-18")]
    [InlineData("from=2026-10-19&to=2026-10-19")]
    [InlineData("from=not-a-date")]
    [InlineData("branchId=not-a-guid")]
    public async Task AnInvalidQuery_Is400ProblemJson(string query)
    {
        using var f = await NewTenantAsync();

        var response = await f.Tenant.Owner.Client.GetAsync($"{Url}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ThePageSize_100IsAccepted()
    {
        using var f = await NewTenantAsync();

        var page = await ListOkAsync(f.Tenant.Owner.Client, "pageSize=100");

        Assert.Equal(100, page.PageSize);
    }

    // ---- Detail -------------------------------------------------------------------------------

    [Fact]
    public async Task TheDetail_HasTheMessagesInTimeOrder_WithTheirText()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0, In(120, "tercero"), In(0, "primero"), Out(60, "segundo"));

        var response = await f.Tenant.Owner.Client.GetAsync($"{Url}/{seeded.ConversationId}");
        var detail = (await response.Content.ReadFromJsonAsync<ConversationDetailResponse>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["primero", "segundo", "tercero"], detail.Messages.Select(m => m.Text));
        Assert.Equal(["Inbound", "Outbound", "Inbound"], detail.Messages.Select(m => m.Direction));
        Assert.Equal(3, detail.MessageCount);
        Assert.Equal("New", detail.Opportunity.Stage);
    }

    [Fact]
    public async Task AMessageWithoutText_ShowsANullText()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedConversationAsync(_factory.Services, f, D0, [new Msg(MessageDirection.Inbound, 0, null, "audio")]);

        var detail = (await f.Tenant.Owner.Client.GetFromJsonAsync<ConversationDetailResponse>($"{Url}/{seeded.ConversationId}"))!;

        var message = Assert.Single(detail.Messages);
        Assert.Equal(("audio", (string?)null), (message.Type, message.Text));
    }

    [Fact]
    public async Task AnUnknownConversation_Is404WithASpanishProblem()
    {
        using var f = await NewTenantAsync();

        var response = await f.Tenant.Owner.Client.GetAsync($"{Url}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemJsonAsync(response);
        Assert.Equal("Conversación no encontrada", problem.Title);
    }

    // ---- Tenant isolation ---------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenantsConversations_AreInvisible_WithTheSame404AsAnUnknownId()
    {
        using var a = await NewTenantAsync();
        using var b = await NewTenantAsync();
        var seeded = await SeedAsync(a, D0);
        var advisor = await CreateAdvisorAsync(b.Tenant.Owner.Client, b.Branch.Id);

        Assert.Equal(HttpStatusCode.OK, (await a.Tenant.Owner.Client.GetAsync($"{Url}/{seeded.ConversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.Client.GetAsync($"{Url}/{seeded.ConversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = advisor.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Won" })).StatusCode);
        Assert.Empty((await ListOkAsync(b.Tenant.Owner.Client)).Items);
        Assert.Equal(1, (await ListOkAsync(a.Tenant.Owner.Client)).TotalCount);
        Assert.Equal("New", (await a.Tenant.Owner.Client.GetFromJsonAsync<ConversationDetailResponse>($"{Url}/{seeded.ConversationId}"))!.Opportunity.Stage);
    }

    [Fact]
    public async Task TheListFilters_CannotReachAnotherTenantsBranchOrAdvisor()
    {
        using var a = await NewTenantAsync();
        using var b = await NewTenantAsync();
        await SeedAsync(a, D0);

        Assert.Empty((await ListOkAsync(b.Tenant.Owner.Client, $"branchId={a.Branch.Id}")).Items);
        Assert.Single((await ListOkAsync(a.Tenant.Owner.Client, $"branchId={a.Branch.Id}")).Items);
    }

    // ---- Permissions --------------------------------------------------------------------------

    public enum Op { List, Get, AssignAdvisor, SetOutcome }

    public static TheoryData<Op> AllOps => new(Enum.GetValues<Op>());

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, Op op, Seeded seeded, Guid advisorId) => op switch
    {
        Op.List => client.GetAsync(Url),
        Op.Get => client.GetAsync($"{Url}/{seeded.ConversationId}"),
        Op.AssignAdvisor => client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId }),
        _ => client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "InFollowUp" }),
    };

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Anonymous_Is401_WhileTheOwnerSucceeds(Op op)
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RunAsync(_factory.CreateClient(), op, seeded, advisor.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RunAsync(f.Tenant.Owner.Client, op, seeded, advisor.Id)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task WithoutTheConversationPermissions_Is403_EvenWithOtherPermissions(Op op)
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);
        using var none = await f.Tenant.AddMemberAsync("none");
        using var others = await f.Tenant.AddMemberAsync("others", Permission.OrganizationView, Permission.OrganizationManage, Permission.UsersView, Permission.ProductsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(none.Client, op, seeded, advisor.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(others.Client, op, seeded, advisor.Id)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task ConversationsView_AllowsExactlyTheReads_AndManageExactlyTheWrites(Op op)
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);
        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.ConversationsView);
        using var manager = await f.Tenant.AddMemberAsync("manager", Permission.ConversationsManage);
        var isRead = op is Op.List or Op.Get;

        Assert.Equal(isRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await RunAsync(viewer.Client, op, seeded, advisor.Id)).StatusCode);
        Assert.Equal(isRead ? HttpStatusCode.Forbidden : HttpStatusCode.OK, (await RunAsync(manager.Client, op, seeded, advisor.Id)).StatusCode);
    }

    // ---- Advisor assignment -------------------------------------------------------------------

    [Fact]
    public async Task AssignAdvisor_OfTheConversationsBranch_Works_AndNullUnassigns()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);
        var client = f.Tenant.Owner.Client;

        var assigned = await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = advisor.Id });
        var body = (await assigned.Content.ReadFromJsonAsync<ConversationListItem>())!;
        Assert.Equal((HttpStatusCode.OK, advisor.Id, advisor.Name), (assigned.StatusCode, body.AdvisorId, body.AdvisorName));
        Assert.Equal(advisor.Id, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.AdvisorId);

        var cleared = await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.AdvisorId);
    }

    [Fact]
    public async Task AssignAdvisor_OfAnotherBranch_Is400InSpanish_AndChangesNothing()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var otherBranch = await CreateBranchAsync(f.Tenant.Owner.Client);
        var advisor = await CreateAdvisorAsync(f.Tenant.Owner.Client, otherBranch.Id);

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = advisor.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("El asesor no trabaja en la sucursal de la conversación.", (await ReadProblemJsonAsync(response)).Detail);
        Assert.Null((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.AdvisorId);
    }

    [Fact]
    public async Task AssignAdvisor_OfAnotherTenantOrUnknown_Is400WithTheSameMessage()
    {
        using var a = await NewTenantAsync();
        using var b = await NewTenantAsync();
        var seeded = await SeedAsync(a, D0);
        var foreign = await CreateAdvisorAsync(b.Tenant.Owner.Client, b.Branch.Id);

        var foreignResponse = await a.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = foreign.Id });
        var unknownResponse = await a.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/advisor", new { advisorId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
        Assert.Equal("El asesor indicado no existe.", (await ReadProblemJsonAsync(foreignResponse)).Detail);
        Assert.Equal("El asesor indicado no existe.", (await ReadProblemJsonAsync(unknownResponse)).Detail);
    }

    // ---- Manual outcome -----------------------------------------------------------------------

    [Fact]
    public async Task SetOutcome_Won_IsManual_AndClosesTheOpportunity()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Won" });

        var body = (await response.Content.ReadFromJsonAsync<OpportunityResponse>())!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("Won", "Manual"), (body.Stage, body.StageSource));
        Assert.NotNull(body.ClosedAt);
        Assert.NotNull(body.StageChangedAt);
        var (_, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((OpportunityStage.Won, StageSource.Manual), (opportunity.Stage, opportunity.StageSource));
    }

    [Fact]
    public async Task SetOutcome_LostWithAReasonAndDetail_IsStored()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync(
            $"{Url}/{seeded.ConversationId}/opportunity", new { stage = "lost", lossReason = "competitor", detail = "  Se fue a otra clínica  " });

        var body = (await response.Content.ReadFromJsonAsync<OpportunityResponse>())!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("Lost", "Competitor", "Se fue a otra clínica"), (body.Stage, body.LossReason, body.LossDetail));
        var detail = (await f.Tenant.Owner.Client.GetFromJsonAsync<ConversationDetailResponse>($"{Url}/{seeded.ConversationId}"))!;
        Assert.Equal(("Lost", "Manual", "Competitor"), (detail.Opportunity.Stage, detail.Opportunity.StageSource, detail.Opportunity.LossReason));
    }

    [Fact]
    public async Task SetOutcome_CanBeChangedAgain_AndInFollowUpReopensIt()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var client = f.Tenant.Owner.Client;
        await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Lost", lossReason = "Price" });

        var response = await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "InFollowUp" });

        var body = (await response.Content.ReadFromJsonAsync<OpportunityResponse>())!;
        Assert.Equal(("InFollowUp", "Manual", (string?)null, (DateTime?)null), (body.Stage, body.StageSource, body.LossReason, body.ClosedAt));
    }

    [Theory]
    [InlineData("{}")] // no stage
    [InlineData("{\"stage\":\"Lost\"}")] // lost needs a reason
    [InlineData("{\"stage\":\"New\"}")]
    [InlineData("{\"stage\":\"2\"}")]
    [InlineData("{\"stage\":\"Almost\"}")]
    [InlineData("{\"stage\":\"Won\",\"lossReason\":\"Price\"}")]
    [InlineData("{\"stage\":\"InFollowUp\",\"detail\":\"no aplica\"}")]
    [InlineData("{\"stage\":\"Lost\",\"lossReason\":\"Cheap\"}")]
    [InlineData("{\"stage\":\"Lost\",\"lossReason\":\"6\"}")]
    [InlineData("{\"stage\":\"Lost\",\"lossReason\":\"Other\",\"detail\":\"a\\u0000b\"}")]
    public async Task SetOutcome_WithAnInvalidBody_Is400_AndChangesNothing(string json)
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);

        var response = await f.Tenant.Owner.Client.PutAsync($"{Url}/{seeded.ConversationId}/opportunity", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var (_, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((OpportunityStage.New, StageSource.Auto), (opportunity.Stage, opportunity.StageSource));
    }

    [Fact]
    public async Task SetOutcome_TheDetailBoundary_500Passes_501Fails()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var client = f.Tenant.Owner.Client;

        var ok = await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Lost", lossReason = "Other", detail = new string('a', 500) });
        var tooLong = await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Lost", lossReason = "Other", detail = new string('a', 501) });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        await AssertValidationErrorUnderAsync(tooLong, "detail");
    }

    [Fact]
    public async Task ARejectedOutcome_DoesNotUndoTheEarlierManualDecision()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedAsync(f, D0);
        var client = f.Tenant.Owner.Client;
        await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Won" });

        await client.PutAsJsonAsync($"{Url}/{seeded.ConversationId}/opportunity", new { stage = "Lost" });

        Assert.Equal("Won", (await LoadAsync(_factory.Services, seeded.ConversationId)).Opportunity.Stage.ToString());
    }
}
