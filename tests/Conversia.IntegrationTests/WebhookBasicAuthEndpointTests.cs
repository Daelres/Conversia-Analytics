using System.Net;
using System.Net.Http.Json;
using System.Text;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The webhook also accepts the channel secret as the password of <c>Authorization: Basic</c> (for a provider that forwards only that
/// header). Same checks as <c>X-Webhook-Secret</c>, same uniform 401, never the URL, never in a log.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookBasicAuthEndpointTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookBasicAuthEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<ChannelFixture> NewChannelAsync() => StartChannelAsync(_factory, _client);

    private Task<Snapshot> SnapshotOfAsync(ChannelFixture f) => SnapshotAsync(_factory.Services, f.TenantId);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? secretHeader, string? authorization, string? body = null, string? clientIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body ?? Dialog360Samples.InboundText($"wamid.BA-{Guid.NewGuid():N}"), Encoding.UTF8, "application/json"),
        };
        if (secretHeader is not null)
        {
            request.Headers.Add("X-Webhook-Secret", secretHeader);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (clientIp is not null)
        {
            request.Headers.Add(TestClientIp.HeaderName, clientIp);
        }

        return client.SendAsync(request);
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---- A valid Basic delivery -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("conversia")]
    [InlineData("whatever-user")]
    [InlineData("")]
    public async Task AValidBasicCredential_IsAnswered200_AndStored_WhateverTheUserName(string user)
    {
        using var f = await NewChannelAsync();

        using var response = await PostAsync(_client, f.Path, null, $"Basic {B64($"{user}:{f.Secret}")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single((await SnapshotOfAsync(f)).Events);
        Assert.Equal((WebhookEventStatus.Pending, f.Channel.Id, f.TenantId), (stored.Status, stored.ChannelId, stored.TenantId));
    }

    [Fact]
    public async Task ABasicDelivery_IsProcessedByTheWorkerLikeAnyOther()
    {
        using var f = await NewChannelAsync();
        using var response = await PostAsync(_client, f.Path, null, WebhookBasicAuth.Header(f.Secret), Dialog360Samples.InboundText());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await DrainAsync(RunnerOf(_factory.Services));

        var s = await SnapshotOfAsync(f);
        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
        Assert.Equal("wamid.HBgM-IN-1", Assert.Single(s.Messages).ProviderMessageId);
        Assert.Single(s.Contacts);
        Assert.Single(s.Conversations);
    }

    [Fact]
    public async Task TheSchemeIsCaseInsensitive_AndSurroundingWhitespaceIsTolerated()
    {
        using var f = await NewChannelAsync();

        using var lower = await PostAsync(_client, f.Path, null, $"basic {B64($"conversia:{f.Secret}")}");
        using var padded = await PostAsync(_client, f.Path, null, $"Basic   {B64($"conversia:{f.Secret}")}  ");

        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.OK, padded.StatusCode);
        Assert.Equal(2, (await SnapshotOfAsync(f)).Events.Count);
    }

    // ---- Every failure is the same 401 ---------------------------------------------------------------------------------

    [Fact]
    public async Task EveryBasicFailure_IsTheSame401AsAWrongHeaderSecret_AndStoresNothing()
    {
        using var f = await NewChannelAsync();
        using var other = await NewChannelAsync();
        using var inactive = await NewChannelAsync();
        using var suspended = await NewChannelAsync();
        using var admin = await StartAdminAsync(_factory, _client);
        await Deactivate(inactive.Channel.Id);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{suspended.TenantId}/status", new { status = "Suspended" })).StatusCode);
        var unknownKey = $"/api/webhooks/360dialog/{new string('A', 43)}";

        var attempts = new (string Name, string Path, string? Authorization)[]
        {
            ("wrong password", f.Path, WebhookBasicAuth.Header(f.Secret + "x")),
            ("the password of another channel", f.Path, WebhookBasicAuth.Header(other.Secret)),
            ("wrong scheme (Bearer)", f.Path, $"Bearer {f.Secret}"),
            ("the secret as a bare token", f.Path, $"Basic {f.Secret}"),
            ("not base64", f.Path, "Basic not*base64!"),
            ("unpadded base64", f.Path, $"Basic {B64($"conversia:{f.Secret}").TrimEnd('=')}x"),
            ("no colon", f.Path, $"Basic {B64("conversia" + f.Secret)}"),
            ("empty password", f.Path, $"Basic {B64("conversia:")}"),
            ("not utf8", f.Path, $"Basic {Convert.ToBase64String([0x75, 0x3a, 0xFF, 0xFE])}"),
            ("scheme only", f.Path, "Basic"),
            ("unknown key, valid-looking credential", unknownKey, WebhookBasicAuth.Header(f.Secret)),
            ("malformed key, valid-looking credential", "/api/webhooks/360dialog/short", WebhookBasicAuth.Header(f.Secret)),
            ("inactive channel, its right password", inactive.Path, WebhookBasicAuth.Header(inactive.Secret)),
            ("suspended company, its right password", suspended.Path, WebhookBasicAuth.Header(suspended.Secret)),
        };

        using var reference = await PostAsync(_client, f.Path, "wrong", null);
        var expected = await ReadProblemJsonAsync(reference);
        foreach (var attempt in attempts)
        {
            using var response = await PostAsync(_client, attempt.Path, null, attempt.Authorization);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, attempt.Name);
            var problem = await ReadProblemJsonAsync(response);
            Assert.True((problem.Title, problem.Detail, problem.Type) == (expected.Title, expected.Detail, expected.Type), attempt.Name);
            Assert.Equal(
                reference.Headers.Select(h => h.Key).Where(k => k is not "Date").Order(),
                response.Headers.Select(h => h.Key).Where(k => k is not "Date").Order());
        }

        foreach (var fixture in new[] { f, other, inactive, suspended })
        {
            Assert.Empty((await SnapshotOfAsync(fixture)).Events);
        }
    }

    [Fact]
    public async Task AHeaderOverTheLengthCap_Is401_EvenWhenItWouldBeValid()
    {
        using var f = await NewChannelAsync();
        var tooLong = $"Basic {B64($"conversia:{f.Secret}{new string('a', 1000)}")}";
        Assert.True(tooLong.Length > WebhookBasicAuth.MaxHeaderLength);

        using var response = await PostAsync(_client, f.Path, null, tooLong);
        using var huge = await PostAsync(_client, f.Path, null, "Basic " + new string('A', 20_000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, huge.StatusCode);
        Assert.Empty((await SnapshotOfAsync(f)).Events);
    }

    // ---- Never the URL ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("secret")]
    [InlineData("key")]
    [InlineData("password")]
    [InlineData("webhook_secret")]
    public async Task ASecretInTheQueryString_IsIgnored_AndWithoutAHeaderTheCallIs401(string parameter)
    {
        using var f = await NewChannelAsync();

        using var response = await PostAsync(_client, $"{f.Path}?{parameter}={Uri.EscapeDataString(f.Secret)}", null, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty((await SnapshotOfAsync(f)).Events);
    }

    [Fact]
    public async Task AQueryStringSecretDoesNotRescueAWrongHeader_AndAHeaderStillWorksNextToAQuery()
    {
        using var f = await NewChannelAsync();

        using var wrong = await PostAsync(_client, $"{f.Path}?secret={Uri.EscapeDataString(f.Secret)}", "wrong", null);
        using var right = await PostAsync(_client, $"{f.Path}?secret=junk", f.Secret, null);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    // ---- Both headers: X-Webhook-Secret first, then the Basic password ----------------------------------------------------

    [Fact]
    public async Task WhenBothHeadersArePresent_EitherOneThatIsValidAuthenticates_AndBothInvalidIs401()
    {
        using var f = await NewChannelAsync();
        using var other = await NewChannelAsync();

        using var bothValid = await PostAsync(_client, f.Path, f.Secret, WebhookBasicAuth.Header(f.Secret));
        using var headerValidBasicWrong = await PostAsync(_client, f.Path, f.Secret, WebhookBasicAuth.Header("wrong"));
        using var headerValidBasicMalformed = await PostAsync(_client, f.Path, f.Secret, "Basic !!!");
        using var headerWrongBasicValid = await PostAsync(_client, f.Path, "wrong", WebhookBasicAuth.Header(f.Secret));
        using var bothWrong = await PostAsync(_client, f.Path, "wrong", WebhookBasicAuth.Header("wrong"));
        using var headerOfOtherBasicWrong = await PostAsync(_client, f.Path, other.Secret, WebhookBasicAuth.Header(other.Secret));

        Assert.Equal(HttpStatusCode.OK, bothValid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, headerValidBasicWrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, headerValidBasicMalformed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, headerWrongBasicValid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bothWrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, headerOfOtherBasicWrong.StatusCode);
        Assert.Equal(4, (await SnapshotOfAsync(f)).Events.Count);
        Assert.Empty((await SnapshotOfAsync(other)).Events);
    }

    // ---- Throttling ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedBasicAuthentications_CountTowardTheAddressThrottle_AndOnceEachWhateverWasPresented()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:FailedAuthRateLimitPerMinute"] = "3" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        var address = TestClientIp.Next();

        var statuses = new List<HttpStatusCode>();
        // A wrong password, a malformed credential and both headers wrong: one failure each.
        string?[][] attempts = [[null, WebhookBasicAuth.Header("wrong")], [null, "Basic !!!"], ["wrong", WebhookBasicAuth.Header("wrong")], [null, WebhookBasicAuth.Header("wrong")], [null, WebhookBasicAuth.Header("wrong")]];
        foreach (var attempt in attempts)
        {
            using var response = await PostAsync(client, f.Path, attempt[0], attempt[1], clientIp: address);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        using var blockedRight = await PostAsync(client, f.Path, null, WebhookBasicAuth.Header(f.Secret), clientIp: address);
        using var otherAddress = await PostAsync(client, f.Path, null, WebhookBasicAuth.Header(f.Secret), clientIp: TestClientIp.Next());
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedRight.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherAddress.StatusCode);
    }

    [Fact]
    public async Task BasicDeliveries_AreLimitedPerChannelLikeAnyOther_AndDoNotSpendTheJunkBudget()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:FailedAuthRateLimitPerMinute"] = "2", ["Webhooks:ChannelRateLimitPerMinute"] = "3" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        var address = TestClientIp.Next();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await PostAsync(client, f.Path, null, WebhookBasicAuth.Header(f.Secret), clientIp: address);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task AnOversizedBodyWithABadBasicCredential_Is401_NotEvenRead()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:MaxBodyBytes"] = "1024" });
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);

        using var response = await PostAsync(client, f.Path, null, WebhookBasicAuth.Header("wrong"), new string('x', 5000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Nothing sensitive in logs nor in the error registry ----------------------------------------------------------

    [Fact]
    public async Task MalformedAndWrongBasicHeaders_LeaveNothingSensitiveInTheLogsNorTheErrorRegistry()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), configureLogging: logging => logging.AddProvider(capture));
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        var marker = $"MARKER{Guid.NewGuid():N}";
        var started = DateTime.UtcNow;
        var headers = new[]
        {
            "Basic " + B64($"user:{marker}"),
            "Basic " + marker,
            "Basic " + B64($"nocolon{marker}"),
            "Bearer " + marker,
            "Basic !!!" + marker,
        };

        foreach (var header in headers)
        {
            using var response = await PostAsync(client, f.Path, null, header);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var logs = string.Join('\n', capture.Entries.Select(e => e.Text));
        foreach (var leak in new[] { marker, B64($"user:{marker}"), B64($"nocolon{marker}"), f.Secret, B64($"conversia:{f.Secret}") })
        {
            Assert.DoesNotContain(leak, logs);
        }

        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Error);
        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ErrorLogs.AsNoTracking()
            .Where(e => e.OccurredAt >= started && e.Path!.StartsWith("/api/webhooks/360dialog/")).ToListAsync();
        // (Rows of other tests may share the window; what matters is that no row holds the header, the marker or the secret.)
        var registry = string.Concat(rows.Select(e => string.Concat(e.Path, e.RouteValues, e.Query, e.Endpoint, e.ExceptionMessage, e.ExceptionDetails, e.Context, e.UserAgent)));
        Assert.DoesNotContain(marker, registry);
        Assert.DoesNotContain(f.Secret, registry);
        Assert.DoesNotContain(f.Channel.ChannelKey!, registry);
    }

    private sealed class ThrowingStoreIngestion : IWebhookIngestionService
    {
        public Task<WebhookChannelAccess?> AuthenticateAsync(string channelKey, string? secret, CancellationToken cancellationToken = default)
            => Task.FromResult<WebhookChannelAccess?>(new WebhookChannelAccess(Guid.NewGuid(), Guid.NewGuid()));

        public Task<WebhookStoreResult> StoreAsync(WebhookChannelAccess channel, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("storage blew up");
    }

    [Fact]
    public async Task AnUnexpectedFailureOfAnAuthenticatedBasicCall_RegistersNeitherTheAuthorizationHeaderNorTheSecret()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), services => services.AddScoped<IWebhookIngestionService, ThrowingStoreIngestion>(), logging => logging.AddProvider(capture));
        using var client = host.CreateClient();
        var marker = $"MARKER{Guid.NewGuid():N}";
        var header = WebhookBasicAuth.Header(marker);
        var started = DateTime.UtcNow.AddSeconds(-1);

        using var response = await PostAsync(client, $"/api/webhooks/360dialog/Key{Guid.NewGuid():N}{Guid.NewGuid():N}", null, header, "{}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ErrorLogs.AsNoTracking()
            .Where(e => e.OccurredAt >= started && e.Path!.StartsWith("/api/webhooks/360dialog/")).ToListAsync());
        var stored = string.Concat(row.Path, row.RouteValues, row.Query, row.Endpoint, row.ExceptionMessage, row.ExceptionDetails, row.Context, row.UserAgent);
        var logs = string.Join('\n', capture.Entries.Select(e => e.Text));
        foreach (var text in new[] { stored, logs, await response.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(marker, text);
            Assert.DoesNotContain(header, text);
            Assert.DoesNotContain(header["Basic ".Length..], text);
        }
    }

    private async Task Deactivate(Guid channelId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE \"WhatsAppChannels\" SET \"IsActive\" = false WHERE \"Id\" = {channelId}");
    }
}
