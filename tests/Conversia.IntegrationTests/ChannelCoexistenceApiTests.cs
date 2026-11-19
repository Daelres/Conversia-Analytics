using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The coexistence settings of a channel through the API (docs/MESSAGING.md, "Coexistence"): the history import opt-in, only for the owner
/// and only with the legal basis confirmed, and the disconnection flag in the channel list.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ChannelCoexistenceApiTests(CustomWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string Url(Guid channelId) => $"/api/channels/{channelId}/history-import";

    [Fact]
    public async Task TheOwner_TurnsTheImportOn_ConfirmingTheLegalBasis_AndTheChannelSaysWhoAndWhen()
    {
        using var f = await StartChannelAsync(factory, _client);

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync(Url(f.Channel.Id), new { enabled = true, legalBasisConfirmed = true, analyzeWithAi = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channel = await ReadBodyAsync<ChannelResponse>(response);
        Assert.True(channel.HistoryImportEnabled);
        Assert.False(channel.HistoryAiAnalysisEnabled);
        Assert.Equal(f.Tenant.Owner.UserId, channel.HistoryImportConsentedBy);
        Assert.NotNull(channel.HistoryImportConsentedAt);
    }

    [Fact]
    public async Task WithoutConfirmingTheLegalBasis_ItStaysOff()
    {
        using var f = await StartChannelAsync(factory, _client);

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync(Url(f.Channel.Id), new { enabled = true, legalBasisConfirmed = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var channels = await f.Tenant.Owner.Client.GetFromJsonAsync<List<ChannelResponse>>("/api/channels");
        Assert.False(channels!.Single(c => c.Id == f.Channel.Id).HistoryImportEnabled);
    }

    [Fact]
    public async Task AManagerWhoIsNotTheOwner_CannotTurnItOn()
    {
        using var f = await StartChannelAsync(factory, _client);
        using var manager = await f.Tenant.AddMemberAsync("history-manager", Permission.OrganizationManage, Permission.OrganizationView);

        var response = await manager.Client.PutAsJsonAsync(Url(f.Channel.Id), new { enabled = true, legalBasisConfirmed = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TurningItOff_NeedsNoConfirmation()
    {
        using var f = await StartChannelAsync(factory, _client);
        await f.Tenant.Owner.Client.PutAsJsonAsync(Url(f.Channel.Id), new { enabled = true, legalBasisConfirmed = true });

        var response = await f.Tenant.Owner.Client.PutAsJsonAsync(Url(f.Channel.Id), new { enabled = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadBodyAsync<ChannelResponse>(response)).HistoryImportEnabled);
    }

    [Fact]
    public async Task AChannelDisconnectedByCoexistence_IsFlaggedInTheList_WithoutContent()
    {
        using var f = await StartChannelAsync(factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.AccountUpdate("PARTNER_REMOVED", "PRIMARY_INACTIVITY"));
        await DrainAsync(RunnerOf(factory.Services));

        var channels = await f.Tenant.Owner.Client.GetFromJsonAsync<List<ChannelResponse>>("/api/channels");

        var channel = channels!.Single(c => c.Id == f.Channel.Id);
        Assert.True(channel.ProviderDisconnected);
        Assert.Equal(("PARTNER_REMOVED", "PRIMARY_INACTIVITY"), (channel.ProviderDisconnectionEvent, channel.ProviderDisconnectionReason));
    }

    [Fact]
    public async Task TheAdministrator_SeesTheDisconnectedLine_AsACountAndAFlag()
    {
        using var f = await StartChannelAsync(factory, _client);
        using var admin = await StartAdminAsync(factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.AccountUpdate("ACCOUNT_OFFBOARDED"));
        await DrainAsync(RunnerOf(factory.Services));

        var tenant = await admin.Client.GetFromJsonAsync<Application.DTOs.Admin.TenantMonitoringResponse>($"/api/admin/tenants/{f.TenantId}/monitoring");
        var platform = await admin.Client.GetFromJsonAsync<Application.DTOs.Admin.PlatformOverviewResponse>("/api/admin/monitoring");

        Assert.Equal(1, tenant!.Backlog.DisconnectedChannels);
        Assert.Contains(platform!.Flags, flag => flag.Code == "coexistence_disconnected");
    }
}
