using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The administrator's API over the companies: listing, detail, status, quota, forcing a line off, and the audit of each change.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformAdminTenantsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformAdminTenantsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string StatusUrl(Guid id) => $"{AdminTenantsUrl}/{id}/status";

    private static string QuotaUrl(Guid id) => $"{AdminTenantsUrl}/{id}/line-quota";

    private async Task<(TestAdmin Admin, TestTenant Tenant)> PendingAsync()
        => (await StartAdminAsync(_factory, _client), await StartTenantAsync(_factory, _client, "pending", pending: true));

    // ---- Listing and detail -------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsAPendingCompanyWithItsOwnerAndNoLines_AndFiltersByStatusAndName()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;
        var name = (await GetAdminTenantAsync(admin, tenant.TenantId)).Name;

        var byName = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?search={Uri.EscapeDataString(name.ToLowerInvariant())}");
        var pendingOnly = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?status=Pending&search={Uri.EscapeDataString(name)}");
        var activeOnly = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?status=Active&search={Uri.EscapeDataString(name)}");

        var item = Assert.Single(byName!.Items, t => t.Id == tenant.TenantId);
        Assert.Equal((TenantStatus.Pending, 0, 0, 0), (item.Status, item.MaxActiveLines, item.ActiveLines, item.RegisteredLines));
        Assert.Equal(tenant.Session.OwnerEmail, item.OwnerEmail);
        Assert.Null(item.LastActivityAt);
        Assert.Contains(pendingOnly!.Items, t => t.Id == tenant.TenantId);
        Assert.Empty(activeOnly!.Items);
    }

    [Fact]
    public async Task List_SearchTreatsPercentAndUnderscoreAsPlainCharacters()
    {
        using var admin = await StartAdminAsync(_factory, _client);

        var all = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?search=%25");
        var underscore = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?search=_");

        Assert.Empty(all!.Items);
        Assert.Empty(underscore!.Items);
    }

    [Fact]
    public async Task List_PagesAtMost100_AndRefusesBadPaging()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var a = await StartTenantAsync(_factory, _client);
        using var b = await StartTenantAsync(_factory, _client);

        var page = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?page=1&pageSize=1");
        Assert.Single(page!.Items);
        Assert.True(page.TotalCount >= 2);
        Assert.Equal((1, 1), (page.Page, page.PageSize));

        foreach (var query in new[] { "pageSize=101", "pageSize=0", "page=0", "page=-1", "status=Nonsense", "status=7" })
        {
            await AssertValidationErrorUnderAsync(await admin.Client.GetAsync($"{AdminTenantsUrl}?{query}"), query.Split('=')[0]);
        }
    }

    [Fact]
    public async Task Detail_ListsTheLinesWithTheirBranchAndState_AndTheActivity()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "detail");
        var branch = await CreateBranchAsync(tenant.Owner.Client, "Sede Norte");
        var on = await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 111 0001");
        var off = await CreateInactiveChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 111 0002");
        var sentAt = DateTime.UtcNow.AddHours(-3);
        await SeedConversationAsync(_factory.Services, tenant.TenantId, branch.Id, on.Id, sentAt, [In(0)]);

        var detail = await ReadBodyAsync<TenantDetailResponse>(await admin.Client.GetAsync($"{AdminTenantsUrl}/{tenant.TenantId}"));

        Assert.Equal((1, 2), (detail.ActiveLines, detail.RegisteredLines));
        Assert.Equal(2, detail.Lines.Count);
        Assert.Contains(detail.Lines, l => l.ChannelId == on.Id && l.IsActive && l.BranchName == "Sede Norte" && l.DisplayPhoneNumber == "+57 300 111 0001");
        Assert.Contains(detail.Lines, l => l.ChannelId == off.Id && !l.IsActive);
        Assert.InRange(detail.LastActivityAt!.Value, sentAt.AddSeconds(-2), sentAt.AddSeconds(2));
    }

    [Fact]
    public async Task UnknownIds_AreTheSame404_WhateverTheEndpoint()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var unknown = Guid.NewGuid();

        var responses = new[]
        {
            await admin.Client.GetAsync($"{AdminTenantsUrl}/{unknown}"),
            await admin.Client.PutAsJsonAsync(StatusUrl(unknown), new { status = "Active" }),
            await admin.Client.PutAsJsonAsync(QuotaUrl(unknown), new { maxActiveLines = 3 }),
            await admin.Client.PostAsync($"{AdminTenantsUrl}/{unknown}/channels/{Guid.NewGuid()}/deactivate", null),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(async r => (await ReadProblemJsonAsync(r)).Detail));
        Assert.Single(bodies.Distinct());
        Assert.Equal("Empresa no encontrada.", bodies[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync($"{AdminTenantsUrl}/not-a-guid")).StatusCode);
    }

    // ---- Status --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Activating_RecordsWhoWhenAndWhy_AndSuspendingAndReactivatingWork()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        var activated = await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "active", reason = "  Contract 2026-10  " });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var summary = await ReadBodyAsync<TenantSummaryResponse>(activated);
        Assert.Equal((TenantStatus.Active, "Contract 2026-10"), (summary.Status, summary.StatusReason));
        Assert.NotNull(summary.StatusChangedAt);

        Assert.Equal(TenantStatus.Suspended, (await ReadBodyAsync<TenantSummaryResponse>(await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Suspended", reason = "Unpaid" }))).Status);
        var back = await ReadBodyAsync<TenantSummaryResponse>(await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Active" }));
        Assert.Equal((TenantStatus.Active, null), (back.Status, back.StatusReason));
    }

    [Fact]
    public async Task APendingCompany_CanBeSuspendedDirectly()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        var response = await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Suspended" });

        Assert.Equal(TenantStatus.Suspended, (await ReadBodyAsync<TenantSummaryResponse>(response)).Status);
    }

    [Theory]
    [InlineData("Pending", "Pending")]
    [InlineData("Pending", "Active", "Active")]
    [InlineData("Active", "Pending")]
    [InlineData("Suspended", "Pending")]
    [InlineData("Suspended", "Suspended")]
    public async Task InvalidTransitions_Are409_AndChangeNothing(string setup, string target, string? alsoSetup = null)
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;
        foreach (var step in new[] { setup, alsoSetup }.Where(s => s is not null && s != "Pending"))
        {
            // Walk the company to the starting state (Active twice would itself be a 409, which is the case being tested).
            await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = step });
        }

        var before = await GetAdminTenantAsync(admin, tenant.TenantId);
        var response = await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = target });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No se puede pasar la empresa", (await ReadProblemJsonAsync(response)).Detail);
        Assert.Equal(before.Status, (await GetAdminTenantAsync(admin, tenant.TenantId)).Status);
    }

    [Theory]
    [InlineData("{\"status\":\"Archived\"}")]
    [InlineData("{\"status\":\"1\"}")]
    [InlineData("{\"status\":\"\"}")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"Active\",\"reason\":\"bad\\u0000reason\"}")]
    public async Task Status_WithInvalidInput_Is400(string json)
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        var response = await admin.Client.PutAsync(StatusUrl(tenant.TenantId), new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TenantStatus.Pending, (await GetAdminTenantAsync(admin, tenant.TenantId)).Status);
    }

    [Fact]
    public async Task Status_ReasonOver500Characters_Is400()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Active", reason = new string('x', 501) }), "reason");
    }

    // ---- Quota ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Quota_IsSetWithinTheRange_AndRefusedOutsideIt()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        foreach (var ok in new[] { 4, 0, 1000 })
        {
            var response = await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = ok });
            Assert.Equal(ok, (await ReadBodyAsync<TenantSummaryResponse>(response)).MaxActiveLines);
        }

        foreach (var bad in new[] { -1, 1001, int.MaxValue })
        {
            await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = bad }), "maxActiveLines");
        }

        await AssertValidationErrorUnderAsync(await admin.Client.PutAsync(QuotaUrl(tenant.TenantId), new StringContent("{}", System.Text.Encoding.UTF8, "application/json")), "maxActiveLines");
        Assert.Equal(1000, (await GetAdminTenantAsync(admin, tenant.TenantId)).MaxActiveLines);
    }

    [Fact]
    public async Task Quota_CannotGoBelowTheActiveLines_AndTheMessageSaysHowManyAreActive()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "quota");
        var branch = await CreateBranchAsync(tenant.Owner.Client);
        await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 222 0001");
        await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 222 0002");
        await CreateInactiveChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 222 0003"); // registered lines do not count, active ones do

        var refused = await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = 1 });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("2 activa(s)", (await ReadProblemJsonAsync(refused)).Detail);
        var exact = await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = 2 });
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
    }

    // ---- Forcing a line off --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAdministrator_CanSwitchALineOff_Idempotently_AndOnlyOfThatCompany()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "force");
        using var other = await StartTenantAsync(_factory, _client, "other");
        var branch = await CreateBranchAsync(tenant.Owner.Client);
        var otherBranch = await CreateBranchAsync(other.Owner.Client);
        var line = await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 333 0001");
        var otherLine = await CreateChannelAsync(other.Owner.Client, otherBranch.Id, "+57 300 333 0002");

        var off = await admin.Client.PostAsync($"{AdminTenantsUrl}/{tenant.TenantId}/channels/{line.Id}/deactivate", null);
        var again = await admin.Client.PostAsync($"{AdminTenantsUrl}/{tenant.TenantId}/channels/{line.Id}/deactivate", null);
        var wrongCompany = await admin.Client.PostAsync($"{AdminTenantsUrl}/{tenant.TenantId}/channels/{otherLine.Id}/deactivate", null);

        Assert.False((await ReadBodyAsync<TenantLineResponse>(off)).IsActive);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongCompany.StatusCode);
        var stillOn = await other.Owner.Client.GetFromJsonAsync<Conversia.Application.DTOs.Organization.ChannelResponse>($"{ChannelsUrl}/{otherLine.Id}");
        Assert.True(stillOn!.IsActive);
        var mine = await tenant.Owner.Client.GetFromJsonAsync<Conversia.Application.DTOs.Organization.ChannelResponse>($"{ChannelsUrl}/{line.Id}");
        Assert.False(mine!.IsActive);
    }

    // ---- Audit ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryChange_WritesOneAuditRow_WithWhoWhatAndOldAndNewValues_AndNoOpsWriteNone()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Active", reason = "Signed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = 3 })).StatusCode);
        var branch = await CreateBranchAsync(tenant.Owner.Client);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(QuotaUrl(tenant.TenantId), new { maxActiveLines = 3 })).StatusCode); // same value: no change
        var line = await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 444 0001");
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync($"{AdminTenantsUrl}/{tenant.TenantId}/channels/{line.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync($"{AdminTenantsUrl}/{tenant.TenantId}/channels/{line.Id}/deactivate", null)).StatusCode); // already off: no change
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PutAsJsonAsync(StatusUrl(tenant.TenantId), new { status = "Pending" })).StatusCode); // refused: no row

        var audit = await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}");

        Assert.Equal(3, audit!.TotalCount);
        var byAction = audit.Items.ToDictionary(e => e.Action);
        Assert.All(audit.Items, e => Assert.Equal((admin.AdminId, admin.Email, tenant.TenantId), (e.AdminId, e.AdminEmail, e.TenantId)));
        Assert.Equal("Pending", byAction["tenant.status_changed"].Details.GetProperty("from").GetString());
        Assert.Equal("Active", byAction["tenant.status_changed"].Details.GetProperty("to").GetString());
        Assert.Equal("Signed", byAction["tenant.status_changed"].Details.GetProperty("reason").GetString());
        Assert.Equal((0, 3), (byAction["tenant.quota_changed"].Details.GetProperty("from").GetInt32(), byAction["tenant.quota_changed"].Details.GetProperty("to").GetInt32()));
        Assert.Equal(line.Id, byAction["channel.deactivated"].Details.GetProperty("channelId").GetGuid());
        Assert.True(audit.Items[0].OccurredAt >= audit.Items[^1].OccurredAt); // newest first

        // The detail of the company shows the same trail, and no secret is anywhere in it.
        var detail = await ReadBodyAsync<TenantDetailResponse>(await admin.Client.GetAsync($"{AdminTenantsUrl}/{tenant.TenantId}"));
        Assert.Equal(3, detail.RecentAudit.Count);
        var raw = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(line.WebhookSecret, raw);
        Assert.DoesNotContain(line.ChannelKey!, raw);
    }

    [Fact]
    public async Task Audit_IsPaged_FilteredByCompany_AndRefusesBadPaging()
    {
        var (admin, tenant) = await PendingAsync();
        using var _ = admin;
        using var __ = tenant;
        await ActivateTenantAsync(admin, tenant.TenantId, 5);

        var page = await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}&page=2&pageSize=1");
        var other = await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={Guid.NewGuid()}");

        Assert.Equal((2, 1, 2), (page!.Page, page.PageSize, page.TotalCount));
        Assert.Single(page.Items);
        Assert.Empty(other!.Items);
        await AssertValidationErrorUnderAsync(await admin.Client.GetAsync("/api/admin/audit?pageSize=101"), "pageSize");
    }
}
