using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>Turning a channel's webhook off and on, rotating its secret, and who gets to see the key of its URL.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class ChannelLifecycleTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChannelLifecycleTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Url(Guid id, string action) => $"{ChannelsUrl}/{id}/{action}";

    private Task<HttpResponseMessage> DeliverRawAsync(ChannelFixture f, string? secret = null, string? path = null)
        => PostWebhookAsync(_client, path ?? f.Path, secret ?? f.Secret, Dialog360Samples.InboundText($"wamid.LC-{Guid.NewGuid():N}"));

    // ---- Activate and deactivate -------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeactivatedChannel_AnswersTheProvider401_UntilItIsActivatedAgain_AndBothAreIdempotent()
    {
        using var f = await StartChannelAsync(_factory, _client);
        Assert.Equal(HttpStatusCode.OK, (await DeliverRawAsync(f)).StatusCode);

        var off = await f.Tenant.Owner.Client.PostAsync(Url(f.Channel.Id, "deactivate"), null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ReadBodyAsync<ChannelResponse>(off)).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.PostAsync(Url(f.Channel.Id, "deactivate"), null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverRawAsync(f)).StatusCode);
        Assert.False((await f.Tenant.Owner.Client.GetFromJsonAsync<ChannelResponse>($"{ChannelsUrl}/{f.Channel.Id}"))!.IsActive);

        var on = await f.Tenant.Owner.Client.PostAsync(Url(f.Channel.Id, "activate"), null);
        Assert.True((await ReadBodyAsync<ChannelResponse>(on)).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await DeliverRawAsync(f)).StatusCode);
    }

    // ---- Rotating the secret -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RotatingTheSecret_ReturnsTheNewOneOnce_AndTheOldOneStopsWorkingAtOnce()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var oldSecret = f.Secret;

        var response = await f.Tenant.Owner.Client.PostAsync(Url(f.Channel.Id, "rotate-secret"), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var rotated = await ReadBodyAsync<CreateChannelResponse>(response);
        Assert.NotEqual(oldSecret, rotated.WebhookSecret);
        Assert.True(rotated.WebhookSecret.Length >= 32);
        Assert.Equal(f.Channel.ChannelKey, rotated.ChannelKey); // the URL does not change, only the secret

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverRawAsync(f, secret: oldSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeliverRawAsync(f, secret: rotated.WebhookSecret)).StatusCode);

        // Nothing else ever shows a secret.
        var read = await f.Tenant.Owner.Client.GetStringAsync($"{ChannelsUrl}/{f.Channel.Id}");
        var list = await f.Tenant.Owner.Client.GetStringAsync(ChannelsUrl);
        Assert.DoesNotContain(rotated.WebhookSecret, read + list);
        Assert.DoesNotContain("ecret", read, StringComparison.Ordinal);

        // And it rotates again, to a different one.
        var again = await ReadBodyAsync<CreateChannelResponse>(await f.Tenant.Owner.Client.PostAsync(Url(f.Channel.Id, "rotate-secret"), null));
        Assert.NotEqual(rotated.WebhookSecret, again.WebhookSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverRawAsync(f, secret: rotated.WebhookSecret)).StatusCode);
    }

    // ---- Isolation and unknown ids -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenantCannotDeactivateOrRotateAChannel_AndItKeepsWorking()
    {
        using var a = await StartChannelAsync(_factory, _client);
        using var b = await StartChannelAsync(_factory, _client);

        foreach (var action in new[] { "deactivate", "activate", "rotate-secret" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.Client.PostAsync(Url(a.Channel.Id, action), null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Tenant.Owner.Client.PostAsync(Url(Guid.NewGuid(), action), null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await DeliverRawAsync(a)).StatusCode); // same secret, still active
    }

    // ---- The key of the webhook URL is for whoever manages the channels ----------------------------------------------

    [Fact]
    public async Task OnlyThoseWhoCanManageSeeTheKeyAndPath_ViewersSeeTheChannelWithoutThem()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.OrganizationView);
        using var manager = await f.Tenant.AddMemberAsync("manager", Permission.OrganizationView, Permission.OrganizationManage);
        var key = f.Channel.ChannelKey!;

        foreach (var url in new[] { ChannelsUrl, $"{ChannelsUrl}/{f.Channel.Id}" })
        {
            var asViewer = await viewer.Client.GetStringAsync(url);
            Assert.DoesNotContain(key, asViewer);
            Assert.DoesNotContain("webhookPath", asViewer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("channelKey", asViewer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(f.Channel.DisplayPhoneNumber, asViewer); // the rest of the channel is there

            Assert.Contains(key, await manager.Client.GetStringAsync(url));
            Assert.Contains(key, await f.Tenant.Owner.Client.GetStringAsync(url)); // the owner bypasses, as everywhere
        }

        var viewerChannel = JsonSerializer.Deserialize<ChannelResponse>(await viewer.Client.GetStringAsync($"{ChannelsUrl}/{f.Channel.Id}"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(viewerChannel.ChannelKey);
        Assert.Null(viewerChannel.WebhookPath);
        Assert.Equal(f.Channel.Id, viewerChannel.Id);
    }
}
