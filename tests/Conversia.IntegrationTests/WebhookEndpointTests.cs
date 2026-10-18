using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>POST /api/webhooks/360dialog/{channelKey}</c>: anonymous, authenticated by the channel's secret, stores the delivery and answers
/// at once. It never parses or processes anything (the processing tests cover that).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<ChannelFixture> NewChannelAsync() => StartChannelAsync(_factory, _client);

    private Task<Snapshot> SnapshotOfAsync(ChannelFixture f) => SnapshotAsync(_factory.Services, f.TenantId);

    // ---- A valid delivery ------------------------------------------------------------------------

    [Fact]
    public async Task AValidDelivery_IsAnswered200_AndStoredAsAPendingEventOfTheChannelsTenant_WithoutBeingProcessed()
    {
        using var f = await NewChannelAsync();
        var body = Dialog360Samples.InboundText();

        using var response = await PostWebhookAsync(_client, f.Path, f.Secret, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await SnapshotOfAsync(f);
        var stored = Assert.Single(snapshot.Events);
        Assert.Equal(WebhookEventStatus.Pending, stored.Status);
        Assert.Equal(f.Channel.Id, stored.ChannelId);
        Assert.Equal(f.TenantId, stored.TenantId);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(body)), stored.BodyHash);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.ProcessedAt);
        Assert.True(stored.ReceivedAt > DateTime.UtcNow.AddMinutes(-1));
        // jsonb normalizes key order and spacing, so the payload is compared by content, not by text.
        var message = JsonDocument.Parse(stored.RawPayload).RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value").GetProperty("messages")[0];
        Assert.Equal("wamid.HBgM-IN-1", message.GetProperty("id").GetString());

        // Nothing but the event: the request path does no business work.
        Assert.Empty(snapshot.Contacts);
        Assert.Empty(snapshot.Conversations);
        Assert.Empty(snapshot.Messages);
    }

    [Fact]
    public async Task TheSameBodyDeliveredAgain_IsStillAnswered200_AndStoresNothingNew()
    {
        using var f = await NewChannelAsync();
        var body = Dialog360Samples.InboundText();

        await DeliverAsync(_client, f, body);
        await DeliverAsync(_client, f, body);
        await DeliverAsync(_client, f, body);

        Assert.Single((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task AnotherBodyOfTheSameMessage_IsAnotherEvent_BecauseIdempotencyOfBodiesIsByHash()
    {
        using var f = await NewChannelAsync();

        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await DeliverAsync(_client, f, Dialog360Samples.InboundText() + "\n");

        Assert.Equal(2, (await SnapshotOfAsync(f)).Events.Count);
    }

    [Fact]
    public async Task ConcurrentDeliveriesOfTheSameBody_StoreExactlyOneEvent_AndAreAll200()
    {
        using var f = await NewChannelAsync();
        var body = Dialog360Samples.InboundText();

        var statuses = await RunInParallelAsync(Enumerable.Range(0, 10).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => PostWebhookAsync(_client, f.Path, f.Secret, body)));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Single((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task TheSameBodyOnTwoChannels_IsTwoEvents()
    {
        using var a = await NewChannelAsync();
        using var b = await NewChannelAsync();
        var body = Dialog360Samples.InboundText();

        await DeliverAsync(_client, a, body);
        await DeliverAsync(_client, b, body);

        Assert.Single((await SnapshotOfAsync(a)).Events);
        Assert.Single((await SnapshotOfAsync(b)).Events);
    }

    // ---- Authentication: every failure is the same 401 ---------------------------------------------

    [Fact]
    public async Task EveryAuthenticationFailure_IsTheSame401_AndStoresNothing()
    {
        using var f = await NewChannelAsync();
        using var other = await NewChannelAsync();
        using var inactive = await NewChannelAsync();
        await DeactivateAsync(inactive.Channel.Id);
        var body = Dialog360Samples.InboundText();
        var unknownKey = $"/api/webhooks/360dialog/{new string('A', 43)}";

        var attempts = new (string Name, string Path, string? Secret)[]
        {
            ("no secret header", f.Path, null),
            ("empty secret", f.Path, ""),
            ("wrong secret", f.Path, f.Secret + "x"),
            ("the secret of another channel", f.Path, other.Secret),
            ("unknown channel key", unknownKey, f.Secret),
            ("malformed channel key", "/api/webhooks/360dialog/short", f.Secret),
            ("inactive channel with its right secret", inactive.Path, inactive.Secret),
        };

        var answers = new List<(HttpStatusCode Status, string? Title, string? Detail, string? Type)>();
        foreach (var attempt in attempts)
        {
            using var response = await PostWebhookAsync(_client, attempt.Path, attempt.Secret, body);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await ReadProblemJsonAsync(response);
            answers.Add((response.StatusCode, problem.Title, problem.Detail, problem.Type));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, attempt.Name);
        }

        Assert.Single(answers.Distinct()); // nothing tells the cases apart
        Assert.Empty((await SnapshotOfAsync(f)).Events);
        Assert.Empty((await SnapshotOfAsync(other)).Events);
        Assert.Empty((await SnapshotOfAsync(inactive)).Events);
    }

    [Fact]
    public async Task AValidJwtIsNeitherNeededNorEnough()
    {
        using var f = await NewChannelAsync();

        // The owner's bearer token does not replace the channel secret...
        using var ownerClient = AuthorizedClient(_factory, f.Tenant.Session.OwnerToken);
        using var withJwtOnly = await PostWebhookAsync(ownerClient, f.Path, null, Dialog360Samples.InboundText());

        Assert.Equal(HttpStatusCode.Unauthorized, withJwtOnly.StatusCode);
        // ...and an anonymous call with the secret needs no token at all.
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
    }

    [Fact]
    public async Task ASecretIsOnlyGoodForItsOwnChannel_AndNeverWritesToAnotherTenant()
    {
        using var a = await NewChannelAsync();
        using var b = await NewChannelAsync();

        using var response = await PostWebhookAsync(_client, a.Path, b.Secret, Dialog360Samples.InboundText());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty((await SnapshotOfAsync(a)).Events);
        Assert.Empty((await SnapshotOfAsync(b)).Events);
    }

    // ---- Bad bodies ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{\"a\": 1} {\"b\": 2}")]
    public async Task ABodyThatIsNotOneJsonObject_Is400_NeverA500_AndStoresNothing(string body)
    {
        using var f = await NewChannelAsync();

        using var response = await PostWebhookAsync(_client, f.Path, f.Secret, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task JsonThatPostgresJsonbCannotHold_Is400_NeverA500()
    {
        using var f = await NewChannelAsync();

        foreach (var body in new[] { "{\"text\": \"a\\u0000b\"}", "{\"text\": \"\\ud800\"}" })
        {
            using var response = await PostWebhookAsync(_client, f.Path, f.Secret, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty((await SnapshotOfAsync(f)).Events);

        // The control: a customer typing the characters "\u0000" is perfectly storable (the backslash is escaped in the JSON).
        await DeliverAsync(_client, f, "{\"text\": \"\\\\u0000\"}");
        Assert.Single((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task ABodyWithoutAContentType_IsAcceptedLikeAnyOther()
    {
        using var f = await NewChannelAsync();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Dialog360Samples.InboundText()));

        using var response = await PostWebhookAsync(_client, f.Path, f.Secret, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AGetIsNotAllowed()
    {
        using var f = await NewChannelAsync();

        using var response = await _client.GetAsync(f.Path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- Size and rate limits (hosts with their own settings) ------------------------------------------

    [Fact]
    public async Task ABodyOverTheLimit_Is413_WhetherOrNotItAnnouncesItsLength_AndOneAtTheLimitIsStored()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:MaxBodyBytes"] = "1024" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        string Json(int size) => "{\"pad\":\"" + new string('x', size - 10) + "\"}";

        using var announced = await PostWebhookAsync(client, f.Path, f.Secret, Json(2000));
        using var chunked = await PostWebhookAsync(client, f.Path, f.Secret, new UnknownLengthContent(Encoding.UTF8.GetBytes(Json(2000))));
        using var atTheLimit = await PostWebhookAsync(client, f.Path, f.Secret, Json(1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, announced.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        Assert.Single((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task AnOversizedBodyWithoutTheSecret_Is401_NotEvenRead()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:MaxBodyBytes"] = "1024" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);

        using var response = await PostWebhookAsync(client, f.Path, "wrong", new string('x', 5000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TooManyCalls_AreThrottledWith429AndRetryAfter_WhateverTheirSecret()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:RateLimitPerMinute"] = "3" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? throttled = null;
        for (var i = 0; i < 5; i++)
        {
            var response = await PostWebhookAsync(client, f.Path, i % 2 == 0 ? f.Secret : "wrong", Dialog360Samples.InboundText($"wamid.RL-{i}"));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled ??= response;
            }
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.NotNull(throttled!.Headers.RetryAfter);
        Assert.Contains("webhook", (await ReadProblemJsonAsync(throttled)).Detail, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeactivateAsync(Guid channelId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE \"WhatsAppChannels\" SET \"IsActive\" = false WHERE \"Id\" = {channelId}");
    }
}

/// <summary>A body that does not announce its length (sent chunked), like a client that streams.</summary>
internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
