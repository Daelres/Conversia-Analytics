using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Conversia.API.ErrorLogging;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Customers' phone numbers must not reach the logs nor the error registry through the ingestion paths: routine duplicates throw nothing,
/// a failing delivery is logged with the masked description only, and the channel key of the webhook URL (half of its credential) is redacted.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookLogPrivacyTests : IAsyncLifetime
{
    private const string Phone = "573005550101";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookLogPrivacyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    // What is left after removing what legitimately looks like a long number: ids, instants, error codes, trace ids and the channel key.
    private static string WithoutIdentifiers(string text, params string[] known)
    {
        foreach (var value in known)
        {
            text = text.Replace(value, "[known]");
        }

        text = Regex.Replace(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "[guid]");
        text = Regex.Replace(text, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?", "[time]");
        text = Regex.Replace(text, @"ERR-\d{8}-[A-Z0-9]+", "[code]");
        return Regex.Replace(text, "[0-9a-fA-F]{16,}", "[hex]");
    }

    private sealed class ThrowingProcessor : IWebhookEventProcessor
    {
        public Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"database said: key (WaId)=({Phone}) is bad, payload {{\"from\":\"{Phone}\"}}; password=hunter2");
    }

    [Fact]
    public async Task ARoutineRedelivery_ThrowsNothingAndLogsNoWarningsOrErrors()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), configureLogging: logging => logging.AddProvider(capture));
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        var body = Dialog360Samples.InboundText();

        for (var i = 0; i < 4; i++)
        {
            using var response = await PostWebhookAsync(client, f.Path, f.Secret, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events);
        // (The in-process test server has no HTTPS port to redirect to; that warning is the host's, not the ingestion's.)
        var noisy = capture.Entries.Where(e => e.Level >= LogLevel.Warning && !e.Category.EndsWith("HttpsRedirectionMiddleware")).Select(e => $"{e.Category}: {e.Text}").ToList();
        Assert.True(noisy.Count == 0, string.Join('\n', noisy));
    }

    [Fact]
    public async Task AFailingDelivery_LeavesNoPhoneNumberInTheLogs_OnlyTheMaskedDescription()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(
            _factory,
            new() { ["Webhooks:Processing:MaxAttempts"] = "2", ["Webhooks:Processing:RetryBaseDelaySeconds"] = "0" },
            services => services.AddScoped<IWebhookEventProcessor, ThrowingProcessor>(),
            logging => logging.AddProvider(capture));
        using var client = host.CreateClient();
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(client, f, Dialog360Samples.InboundText(waId: Phone, text: $"call me at {Phone}"));
        using (var malformed = await PostWebhookAsync(client, f.Path, f.Secret, $"{{\"phone\": \"{Phone}\", broken"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        await DrainAsync(RunnerOf(host.Services)); // two attempts, then Failed (and registered in the error registry)

        var failed = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events);
        Assert.Equal(WebhookEventStatus.Failed, failed.Status);
        var logged = capture.Entries.Where(e => e.Text.Contains("Webhook event")).ToList();
        Assert.Contains(logged, e => e.Level == LogLevel.Error && e.Text.Contains("failed for good"));
        var all = string.Join('\n', capture.Entries.Select(e => e.Text));
        Assert.DoesNotContain(Phone, all);
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotMatch(@"\d{7,}", WithoutIdentifiers(all, f.Channel.ChannelKey!, f.Secret));
        using var scope = _factory.Services.CreateScope();
        var registered = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ErrorLogs.AsNoTracking()
            .Where(e => e.Context!.Contains(failed.Id.ToString())).ToListAsync();
        Assert.DoesNotContain(Phone, string.Concat(registered.Select(e => e.ExceptionMessage + e.ExceptionDetails + e.Context)));
    }

    // ---- The channel key is redacted ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/webhooks/360dialog/abcDEF123_-abcDEF123_-abcDEF123_-abcd", "/api/webhooks/360dialog/{channelKey}")]
    [InlineData("/API/webhooks/360dialog/secretkey", "/API/webhooks/360dialog/{channelKey}")]
    [InlineData("/api/webhooks/360dialog/key/extra", "/api/webhooks/360dialog/{channelKey}/extra")]
    [InlineData("/api/branches", "/api/branches")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void TheChannelKeyIsRedactedFromPaths(string? path, string? expected)
        => Assert.Equal(expected, RequestPathRedactor.Redact(path));

    private sealed class ExplodingIngestion : IWebhookIngestionService
    {
        public Task<WebhookChannelAccess?> AuthenticateAsync(string channelKey, string? secret, CancellationToken cancellationToken = default)
            => Task.FromResult<WebhookChannelAccess?>(new WebhookChannelAccess(Guid.NewGuid(), Guid.NewGuid()));

        public Task<WebhookStoreResult> StoreAsync(WebhookChannelAccess channel, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"the database quoted {Phone} in its detail");
    }

    [Fact]
    public async Task AnUnexpectedFailureOfTheWebhook_IsRegisteredAndLoggedWithoutTheChannelKeyNorTheCustomersData()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), services => services.AddScoped<IWebhookIngestionService, ExplodingIngestion>(), logging => logging.AddProvider(capture));
        using var client = host.CreateClient();
        var key = $"Key{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var started = DateTime.UtcNow.AddSeconds(-1);

        using var response = await PostWebhookAsync(client, $"/api/webhooks/360dialog/{key}", "whatever", "{}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ErrorLogs.AsNoTracking()
            .Where(e => e.OccurredAt >= started && e.Path!.StartsWith("/api/webhooks/360dialog/")).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("/api/webhooks/360dialog/{channelKey}", row.Path);
        Assert.DoesNotContain(key, string.Concat(row.Path, row.RouteValues, row.Query, row.Endpoint, row.ExceptionMessage, row.ExceptionDetails, row.Context));
        Assert.Contains("{channelKey}", row.RouteValues);
        Assert.Null(row.ExceptionDetails);
        Assert.DoesNotContain(Phone, row.ExceptionMessage);
        var logs = string.Join('\n', capture.Entries.Select(e => e.Text));
        Assert.DoesNotContain(key, logs);
        Assert.DoesNotContain(Phone, logs);
        Assert.Contains(capture.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("Unhandled exception"));
    }
}
