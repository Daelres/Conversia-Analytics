using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Tenants;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>Billing is per line: a line is created switched off, the company switches it on up to its quota, and the quota holds under concurrency.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class LineQuotaTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LineQuotaTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string On(Guid id) => $"{ChannelsUrl}/{id}/activate";

    private async Task<(TestAdmin Admin, TestTenant Tenant, Guid BranchId)> TenantWithQuotaAsync(int quota)
    {
        var admin = await StartAdminAsync(_factory, _client);
        var tenant = await StartTenantAsync(_factory, _client, "quota", pending: true);
        await ActivateTenantAsync(admin, tenant.TenantId, quota);
        return (admin, tenant, (await CreateBranchAsync(tenant.Owner.Client)).Id);
    }

    private async Task<int> ActiveLinesAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().WhatsAppChannels.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId && c.IsActive);
    }

    [Fact]
    public async Task ANewChannel_IsCreatedInactive_WithItsSecretReturnedOnce_AndItsWebhookIsClosed()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(1);
        using var _ = admin;
        using var __ = tenant;

        var created = await CreateInactiveChannelAsync(tenant.Owner.Client, branch);
        var raw = await PostChannelAsync(tenant.Owner.Client, branch, "+57 300 600 0001");
        var fresh = await ReadBodyAsync<CreateChannelResponse>(raw);

        Assert.False(fresh.IsActive);
        Assert.True(fresh.WebhookSecret.Length >= 32);
        Assert.False(created.IsActive);
        using var delivery = await PostWebhookAsync(_client, fresh.WebhookPath!, fresh.WebhookSecret, Conversia.Tests.Shared.Dialog360Samples.InboundText());
        Assert.Equal(HttpStatusCode.Unauthorized, delivery.StatusCode);
    }

    [Fact]
    public async Task Activation_StopsAtTheQuota_WithTheSpanishMessage_AndDeactivatingFreesASlot()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(2);
        using var _ = admin;
        using var __ = tenant;
        var lines = new List<CreateChannelResponse>();
        for (var i = 0; i < 3; i++)
        {
            lines.Add(await CreateInactiveChannelAsync(tenant.Owner.Client, branch, $"+57 300 700 000{i}"));
        }

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync(On(lines[0].Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync(On(lines[1].Id), null)).StatusCode);
        var refused = await tenant.Owner.Client.PostAsync(On(lines[2].Id), null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Tu cupo de líneas activas está completo (2 de 2). Contacta al administrador de Conversia.", (await ReadProblemJsonAsync(refused)).Detail);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync(On(lines[0].Id), null)).StatusCode); // already on: idempotent even when full

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync($"{ChannelsUrl}/{lines[0].Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync(On(lines[2].Id), null)).StatusCode);
        var info = await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant");
        Assert.Equal((2, 2, 3), (info!.MaxActiveLines, info.ActiveLines, info.RegisteredLines));
    }

    [Fact]
    public async Task AQuotaOfZero_AllowsRegisteringLinesButNotSwitchingAnyOn()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(0);
        using var _ = admin;
        using var __ = tenant;
        var line = await CreateInactiveChannelAsync(tenant.Owner.Client, branch);

        var response = await tenant.Owner.Client.PostAsync(On(line.Id), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("(0 de 0)", (await ReadProblemJsonAsync(response)).Detail);
    }

    [Fact]
    public async Task AnotherCompanysChannel_IsA404_AndRotatingTheSecretIsUnchangedByTheQuota()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(0);
        using var _ = admin;
        using var __ = tenant;
        using var other = await StartChannelAsync(_factory, _client);
        var mine = await CreateInactiveChannelAsync(tenant.Owner.Client, branch);

        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.Client.PostAsync(On(other.Channel.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.Client.PostAsync(On(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.PostAsync($"{ChannelsUrl}/{mine.Id}/rotate-secret", null)).StatusCode);
    }

    [Fact]
    public async Task ParallelActivations_NeverExceedTheQuota()
    {
        const int quota = 3;
        const int lines = 12;
        var (admin, tenant, branch) = await TenantWithQuotaAsync(quota);
        using var _ = admin;
        using var __ = tenant;
        var ids = new List<Guid>();
        for (var i = 0; i < lines; i++)
        {
            ids.Add((await CreateInactiveChannelAsync(tenant.Owner.Client, branch, $"+57 300 800 {i:D4}")).Id);
        }

        var results = await RunInParallelAsync(ids.Select(id => (Func<Task<HttpResponseMessage>>)(() => tenant.Owner.Client.PostAsync(On(id), null))));

        Assert.Equal(quota, results.Count(r => r == HttpStatusCode.OK));
        Assert.Equal(lines - quota, results.Count(r => r == HttpStatusCode.Conflict));
        Assert.Equal(quota, await ActiveLinesAsync(tenant.TenantId));
    }

    [Fact]
    public async Task ParallelActivations_ByTwoUsersOfTheSameCompany_StillRespectTheQuota()
    {
        const int quota = 4;
        var (admin, tenant, branch) = await TenantWithQuotaAsync(quota);
        using var _ = admin;
        using var __ = tenant;
        using var manager = await tenant.AddMemberAsync("manager", Permission.OrganizationView, Permission.OrganizationManage);
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add((await CreateInactiveChannelAsync(tenant.Owner.Client, branch, $"+57 300 900 {i:D4}")).Id);
        }

        var calls = ids.Select((id, i) => (Func<Task<HttpResponseMessage>>)(() => (i % 2 == 0 ? tenant.Owner.Client : manager.Client).PostAsync(On(id), null)));
        await RunInParallelAsync(calls);

        Assert.Equal(quota, await ActiveLinesAsync(tenant.TenantId));
    }

    [Fact]
    public async Task LoweringTheQuota_WhileLinesAreBeingSwitchedOn_NeverLeavesMoreActiveLinesThanTheQuota()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(8);
        using var _ = admin;
        using var __ = tenant;
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add((await CreateInactiveChannelAsync(tenant.Owner.Client, branch, $"+57 300 950 {i:D4}")).Id);
        }

        var calls = ids.Select(id => (Func<Task<HttpResponseMessage>>)(() => tenant.Owner.Client.PostAsync(On(id), null)))
            .Append(() => admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/line-quota", new { maxActiveLines = 3 }));
        await RunInParallelAsync(calls);

        var stored = await GetAdminTenantAsync(admin, tenant.TenantId);
        Assert.True(await ActiveLinesAsync(tenant.TenantId) <= stored.MaxActiveLines, $"{await ActiveLinesAsync(tenant.TenantId)} active lines with a quota of {stored.MaxActiveLines}");
    }

    [Fact]
    public async Task SwitchingOnIsRefused_WhenTheCompanyIsSuspended_EvenIfThereIsQuota()
    {
        var (admin, tenant, branch) = await TenantWithQuotaAsync(5);
        using var _ = admin;
        using var __ = tenant;
        var line = await CreateInactiveChannelAsync(tenant.Owner.Client, branch);
        await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Suspended" });

        var response = await tenant.Owner.Client.PostAsync(On(line.Id), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await ActiveLinesAsync(tenant.TenantId));
    }
}
