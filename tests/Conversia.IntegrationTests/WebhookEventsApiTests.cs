using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>GET /api/webhook-events: every delivery the lines received, the outcome of each, and what is never shown.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookEventsApiTests : IAsyncLifetime
{
    private const string Url = "/api/webhook-events";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookEventsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<PagedResponse<WebhookEventItem>> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(query.Length == 0 ? Url : $"{Url}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<WebhookEventItem>>())!;
    }

    private async Task<PagedResponse<WebhookEventItem>> DeliverAndListAsync(ChannelFixture f, params string[] bodies)
    {
        foreach (var body in bodies)
        {
            await DeliverAsync(_client, f, body);
        }

        await DrainAsync(RunnerOf(_factory.Services));
        return await ListAsync(f.Tenant.Owner.Client);
    }

    [Fact]
    public async Task ADeliveryWithAMessage_IsStored_AndSaysSo()
    {
        using var f = await StartChannelAsync(_factory, _client);

        var item = Assert.Single((await DeliverAndListAsync(f, Dialog360Samples.InboundText())).Items);

        Assert.Equal(("Processed", "stored", "Mensajes guardados"), (item.Status, item.OutcomeCode, item.Outcome));
        Assert.Equal((1, 0, 0, 0), (item.StoredMessageCount, item.DuplicateMessageCount, item.SkippedMessageCount, item.StatusCount));
        Assert.Equal((f.Channel.Id, "+57 300 555 0100", f.Branch.Name), (item.Line.ChannelId, item.Line.DisplayPhoneNumber, item.Line.BranchName));
        Assert.NotNull(item.ProcessedAt);
        Assert.Null(item.LastError);
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public async Task AStatusOnlyDelivery_IsReceiptsOnly()
    {
        using var f = await StartChannelAsync(_factory, _client);

        var item = Assert.Single((await DeliverAndListAsync(f, Dialog360Samples.StatusesOnly())).Items);

        Assert.Equal(("receipts", "Solo recibos de entrega"), (item.OutcomeCode, item.Outcome));
        Assert.Equal(0, item.StoredMessageCount);
        Assert.True(item.StatusCount > 0);
    }

    [Fact]
    public async Task ADeliveryWhoseMessagesCannotBeStored_IsSkipped()
    {
        using var f = await StartChannelAsync(_factory, _client);

        var item = Assert.Single((await DeliverAndListAsync(f, Dialog360Samples.InboundFrom("CO.13491208655302741918"))).Items);

        Assert.Equal(("skipped", "Mensajes omitidos"), (item.OutcomeCode, item.Outcome));
        Assert.Equal((0, 1), (item.StoredMessageCount, item.SkippedMessageCount));
    }

    [Fact]
    public async Task ANewBodyWithAKnownMessage_IsRepeated()
    {
        using var f = await StartChannelAsync(_factory, _client);
        // Same provider message id, a different body (another timestamp in the receipt envelope is enough for a different hash).
        var first = Dialog360Samples.InboundText(id: "wamid.DUP-1");
        var second = Dialog360Samples.InboundText(id: "wamid.DUP-1", text: "Hola, quisiera info de depilación láser ");

        var page = await DeliverAndListAsync(f, first, second);

        Assert.Equal(["stored", "repeated"], page.Items.OrderBy(i => i.ReceivedAt).Select(i => i.OutcomeCode));
        Assert.Equal("Mensajes repetidos", page.Items.Single(i => i.OutcomeCode == "repeated").Outcome);
    }

    [Fact]
    public async Task ABodyDeliveredTwice_IsOneDelivery()
    {
        using var f = await StartChannelAsync(_factory, _client);

        var page = await DeliverAndListAsync(f, Dialog360Samples.InboundText(), Dialog360Samples.InboundText());

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task APendingDelivery_AndAFailedOne_ShowTheirStateAndASanitizedError()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await SeedEventAsync(f, WebhookEventStatus.Pending, null);
        await SeedEventAsync(f, WebhookEventStatus.Failed, "InvalidOperationException: duplicate key for 573001234567 and token=abc123SECRET");

        var items = (await ListAsync(f.Tenant.Owner.Client)).Items;

        var failed = items.Single(i => i.Status == "Failed");
        Assert.Equal(("failed", "Falló"), (failed.OutcomeCode, failed.Outcome));
        Assert.DoesNotContain("573001234567", failed.LastError);
        Assert.DoesNotContain("abc123SECRET", failed.LastError);
        Assert.Contains("[num]", failed.LastError);
        var pending = items.Single(i => i.Status == "Pending");
        Assert.Equal(("pending", "Pendiente de procesar"), (pending.OutcomeCode, pending.Outcome));
        Assert.Null(pending.LastError);
    }

    [Fact]
    public async Task TheRawPayload_NeverLeaves()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAndListAsync(f, Dialog360Samples.InboundText(text: "texto-secreto-del-cliente"));

        var body = await f.Tenant.Owner.Client.GetStringAsync(Url);

        Assert.DoesNotContain("texto-secreto-del-cliente", body);
        Assert.DoesNotContain(Dialog360Samples.CustomerWaId, body);
        Assert.DoesNotContain("rawPayload", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bodyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.Secret, body);
        Assert.DoesNotContain(f.Channel.WebhookPath!, body);
    }

    [Fact]
    public async Task Filters_PagingAndOrder()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var other = await AddChannelAsync(f, "pnid-2", "+57 300 555 0222");
        await SeedEventAsync(f, WebhookEventStatus.Failed, "x", f.Channel.Id, received: DateTime.UtcNow.AddMinutes(-3));
        await SeedEventAsync(f, WebhookEventStatus.Processed, null, f.Channel.Id, received: DateTime.UtcNow.AddMinutes(-2));
        await SeedEventAsync(f, WebhookEventStatus.Processed, null, other.Id, received: DateTime.UtcNow.AddMinutes(-1));
        var client = f.Tenant.Owner.Client;

        var all = await ListAsync(client, "pageSize=2");
        var next = await ListAsync(client, "pageSize=2&page=2");

        Assert.Equal(3, all.TotalCount);
        Assert.Equal([other.Id, f.Channel.Id], all.Items.Select(i => i.Line.ChannelId));
        Assert.Equal(["Failed"], next.Items.Select(i => i.Status));
        Assert.Equal(["Failed"], (await ListAsync(client, "status=failed")).Items.Select(i => i.Status));
        Assert.Equal(2, (await ListAsync(client, $"channelId={f.Channel.Id}")).TotalCount);
        Assert.Empty((await ListAsync(client, $"channelId={Guid.NewGuid()}")).Items);
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("status=Exploded")]
    [InlineData("page=0")]
    public async Task AnInvalidQuery_Is400(string query)
    {
        using var f = await StartChannelAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.BadRequest, (await f.Tenant.Owner.Client.GetAsync($"{Url}?{query}")).StatusCode);
    }

    [Fact]
    public async Task ACompanySeesOnlyItsOwnDeliveries_AndNeedsConversationsView()
    {
        using var mine = await StartChannelAsync(_factory, _client);
        using var theirs = await StartChannelAsync(_factory, _client);
        await DeliverAndListAsync(theirs, Dialog360Samples.InboundText());
        using var none = await mine.Tenant.AddMemberAsync("none");
        using var viewer = await mine.Tenant.AddMemberAsync("viewer", Permission.ConversationsView);

        Assert.Empty((await ListAsync(mine.Tenant.Owner.Client)).Items);
        Assert.Single((await ListAsync(theirs.Tenant.Owner.Client)).Items);
        Assert.Empty((await ListAsync(mine.Tenant.Owner.Client, $"channelId={theirs.Channel.Id}")).Items);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(Url)).StatusCode);
    }

    private async Task SeedEventAsync(ChannelFixture f, WebhookEventStatus status, string? error, Guid? channelId = null, DateTime? received = null)
    {
        var webhookEvent = WebhookEvent.Receive(f.TenantId, channelId ?? f.Channel.Id, Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(), "{}", received ?? DateTime.UtcNow);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Add(webhookEvent);
        await db.SaveChangesAsync();
        // Straight to the row: the entity's own transitions need a worker; a sanitized error is what the worker would have stored.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "WebhookEvents" SET "Status" = {status.ToString()}, "LastError" = {error}, "NextAttemptAt" = now() + interval '30 days', "UpdatedAt" = now() - interval '2 days'
            WHERE "Id" = {webhookEvent.Id}
            """);
    }
}
