using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Conversia.Application.Media;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Media;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The media jobs against PostgreSQL and the local encrypted store (docs/MESSAGING.md, "Media"): the download job and its states, limits and
/// retries; the retention purge with the company's own retention; and the deletions (contact suppression, sandbox data, the whole company) that
/// remove the stored objects too. The provider is a fake media client; nothing reaches 360dialog. The files are synthetic bytes.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MediaJobsTests
{
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 1, 2, 3, 4, 5, 6];
    private static readonly byte[] Exe = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MediaHost _host;

    public MediaJobsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = factory.GetOrAddHost("media-jobs", () => MediaHost.Create(factory));
    }

    /// <summary>The provider's media endpoint: what each media id answers, and which ids were asked for.</summary>
    internal sealed class FakeMediaClient : IWhatsAppMediaClient, IWhatsAppMediaClientResolver
    {
        public ConcurrentDictionary<string, MediaDownloadResult> Answers { get; } = new();

        public ConcurrentQueue<string> Asked { get; } = new();

        public ConcurrentQueue<(string Type, string MediaId, string? Caption)> Sent { get; } = new();

        public IWhatsAppMediaClient Resolve(WhatsAppChannel channel, bool sandbox) => sandbox ? new Infrastructure.Messaging.SandboxMediaClient() : this;

        /// <summary>The message id the next media send answers with; a new one when null.</summary>
        public string? NextMessageId { get; set; }

        /// <summary>Runs while the provider "sends" (before the record step): to deliver the echo first, for example.</summary>
        public Func<string, Task>? DuringSend { get; set; }

        /// <summary>The next media send throws, like a transport that breaks after the upload.</summary>
        public bool ThrowOnNextSend { get; set; }

        public Task<MediaDownloadResult> DownloadAsync(WhatsAppChannel channel, string providerMediaId, long maxBytes, string? declaredMimeType = null, CancellationToken cancellationToken = default)
        {
            Asked.Enqueue(providerMediaId);
            var answer = Answers.TryGetValue(providerMediaId, out var a) ? a : MediaDownloadResult.Of(MediaDownloadStatus.NotFound);
            // A fresh stream on every call: the job disposes what it gets.
            var fresh = answer.Content is MemoryStream bytes ? new MediaDownloadResult(answer.Status, bytes.ToArray(), answer.DeclaredMimeType) : answer;
            return Task.FromResult(fresh.Content is { } c && c.Length > maxBytes ? MediaDownloadResult.Of(MediaDownloadStatus.TooLarge) : fresh);
        }

        public Task<MediaUploadResult> UploadAsync(WhatsAppChannel channel, Stream content, string contentType, CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaUploadResult(WhatsAppSendStatus.Sent, "77" + Random.Shared.Next(100000, 999999)));

        public async Task<WhatsAppSendResult> SendMediaAsync(WhatsAppMediaMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextSend)
            {
                ThrowOnNextSend = false;
                throw new InvalidOperationException("the transport broke");
            }

            Sent.Enqueue((message.Type, message.ProviderMediaId, message.Caption));
            var id = NextMessageId ?? $"wamid.MEDIA-{Guid.NewGuid():N}";
            NextMessageId = null;
            if (DuringSend is { } during)
            {
                DuringSend = null;
                await during(id);
            }

            return WhatsAppSendResult.Sent(id);
        }
    }

    internal sealed record MediaHost(WebApplicationFactory<Program> Host, FakeMediaClient Client, OffsetTimeProvider Clock)
    {
        public static MediaHost Create(CustomWebApplicationFactory factory)
        {
            var client = new FakeMediaClient();
            var clock = new OffsetTimeProvider();
            var host = WebhookHost(factory, new Dictionary<string, string> { ["Media:MaxDownloadAttempts"] = "2", [EncryptionKeySetting] = TestEncryptionKey },
                services =>
                {
                    services.AddSingleton<IWhatsAppMediaClientResolver>(client);
                    services.AddSingleton<TimeProvider>(clock);
                });
            return new MediaHost(host, client, clock);
        }

        public IServiceProvider Services => Host.Services;
    }

    /// <summary>The real time, which a test can move forward: what the jobs see as "now".</summary>
    internal sealed class OffsetTimeProvider : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    /// <summary>A store that is down: every write fails, reads find nothing.</summary>
    private sealed class DownStore : IMediaStore
    {
        public int Puts;

        public Task<StoredMediaObject> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Puts);
            throw new IOException("the store is unavailable");
        }

        public Task<MediaContent?> OpenReadAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<MediaContent?>(null);

        public Task DeleteAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default)
            => objectKey.EndsWith("poison", StringComparison.Ordinal) ? throw new IOException("cannot delete") : Task.CompletedTask;

        public Task<int> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public IAsyncEnumerable<StoredObjectInfo> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<StoredObjectInfo>();
    }

    private static string NewMediaId() => "9" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);

    private static string ImageDelivery(string messageId, string mediaId, string waId = Dialog360Samples.CustomerWaId)
        => Dialog360Samples.InboundImage(messageId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60).Replace("\"9999\"", $"\"{mediaId}\"", StringComparison.Ordinal)
            .Replace(Dialog360Samples.CustomerWaId, waId, StringComparison.Ordinal);

    /// <summary>A company with a channel and one received image whose provider answers <paramref name="answer"/>.</summary>
    private async Task<(ChannelFixture Fixture, Guid MessageId, string MediaId)> ReceiveImageAsync(MediaDownloadResult answer, string? waId = null)
    {
        var f = await StartChannelAsync(_factory, _client);
        var mediaId = NewMediaId();
        _host.Client.Answers[mediaId] = answer;
        var providerId = $"wamid.MEDIA-{Guid.NewGuid():N}";
        await DeliverAsync(_client, f, ImageDelivery(providerId, mediaId, waId ?? Dialog360Samples.CustomerWaId));
        await DrainAsync(RunnerOf(_host.Services));
        var message = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Single(m => m.ProviderMessageId == providerId);
        return (f, message.Id, mediaId);
    }

    private async Task DownloadAllAsync()
    {
        var runner = _host.Services.GetRequiredService<MediaDownloadRunner>();
        for (var i = 0; i < 200 && await runner.RunOnceAsync(); i++)
        {
        }
    }

    private async Task<Message> MessageAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private IMediaStore Store => _host.Services.GetRequiredService<IMediaStore>();

    [Fact]
    public async Task AReceivedImage_IsDownloadedChecked_AndStoredEncrypted_UnderItsCompany()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));

        await DownloadAllAsync();

        var message = await MessageAsync(id);
        Assert.Equal((MediaStatus.Stored, "image/jpeg", (long)Jpeg.Length), (message.MediaStatus!.Value, message.MediaContentType, message.MediaSizeBytes!.Value));
        Assert.True(MediaObjectKeys.BelongsTo(f.TenantId, message.MediaObjectKey));
        await using var content = await Store.OpenReadAsync(f.TenantId, message.MediaObjectKey!);
        using var copy = new MemoryStream();
        await content!.Content.CopyToAsync(copy);
        Assert.Equal(Jpeg, copy.ToArray());
    }

    [Fact]
    public async Task AnExecutableSentAsAnImage_IsBlocked_AndNothingIsStored()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Exe, "image/jpeg"));

        await DownloadAllAsync();

        var message = await MessageAsync(id);
        Assert.Equal(MediaStatus.Blocked, message.MediaStatus);
        Assert.Null(message.MediaObjectKey);
        Assert.Equal(0, await Store.DeleteAllAsync(f.TenantId));
    }

    [Fact]
    public async Task AnImageOverItsLimit_IsTooLarge()
    {
        var (_, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, [.. Jpeg, .. new byte[MediaPolicy.MaxBytes(MediaKind.Image)]], "image/jpeg"));

        await DownloadAllAsync();

        Assert.Equal(MediaStatus.TooLarge, (await MessageAsync(id)).MediaStatus);
    }

    [Fact]
    public async Task MediaTheProviderNoLongerHas_IsExpired()
    {
        var (_, id, _) = await ReceiveImageAsync(MediaDownloadResult.Of(MediaDownloadStatus.NotFound));

        await DownloadAllAsync();

        Assert.Equal(MediaStatus.Expired, (await MessageAsync(id)).MediaStatus);
    }

    [Fact]
    public async Task ATransientFailure_IsRetriedLater_AndFailsAfterTheLastAttempt()
    {
        var (_, id, _) = await ReceiveImageAsync(MediaDownloadResult.Of(MediaDownloadStatus.TransientFailure));

        await DownloadAllAsync();

        var first = await MessageAsync(id);
        Assert.Equal((MediaStatus.Pending, 1), (first.MediaStatus!.Value, first.MediaAttempts));
        Assert.True(first.MediaNextAttemptAt >= _host.Clock.GetUtcNow().UtcDateTime.AddSeconds(55)); // the first retry waits Media:RetryBaseSeconds (60)

        await ExecuteAsync($"""UPDATE "Messages" SET "MediaNextAttemptAt" = now() - interval '1 second' WHERE "Id" = {id}""");
        await DownloadAllAsync();
        await ExecuteAsync($"""UPDATE "Messages" SET "MediaNextAttemptAt" = now() - interval '1 second' WHERE "Id" = {id}""");
        await DownloadAllAsync();

        Assert.Equal(MediaStatus.Failed, (await MessageAsync(id)).MediaStatus);
    }

    [Fact]
    public async Task MediaReceivedTooLongAgo_IsExpired_WithoutAskingTheProvider()
    {
        var (_, id, mediaId) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        await ExecuteAsync($"""UPDATE "Messages" SET "CreatedAt" = now() - interval '7 days' WHERE "Id" = {id}""");

        await DownloadAllAsync();

        Assert.Equal(MediaStatus.Expired, (await MessageAsync(id)).MediaStatus);
        Assert.DoesNotContain(mediaId, _host.Client.Asked);
    }

    [Fact]
    public async Task TheRetention_PurgesCopiesOlderThanTheCompanysRetention_AndDeletesTheObjects()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        await DownloadAllAsync();
        var key = (await MessageAsync(id)).MediaObjectKey!;
        await ExecuteAsync($"""UPDATE "Tenants" SET "MediaRetentionDays" = 5 WHERE "Id" = {f.TenantId}""");
        await ExecuteAsync($"""UPDATE "Messages" SET "MediaStoredAt" = now() - interval '4 days' WHERE "Id" = {id}""");

        var retention = _host.Services.GetRequiredService<MediaRetentionRunner>();
        await retention.RunOnceAsync();
        Assert.Equal(MediaStatus.Stored, (await MessageAsync(id)).MediaStatus); // within its 5 days

        await ExecuteAsync($"""UPDATE "Messages" SET "MediaStoredAt" = now() - interval '6 days' WHERE "Id" = {id}""");
        await retention.RunOnceAsync();

        var purged = await MessageAsync(id);
        Assert.Equal(MediaStatus.Purged, purged.MediaStatus);
        Assert.Null(await Store.OpenReadAsync(f.TenantId, key));
    }

    [Fact]
    public async Task SuppressingAContact_DeletesItsStoredObjects()
    {
        var waId = "5730055" + Random.Shared.Next(10000, 99999);
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"), waId);
        await DownloadAllAsync();
        var key = (await MessageAsync(id)).MediaObjectKey!;
        var contactId = (await SnapshotAsync(_factory.Services, f.TenantId)).Contacts.Single(c => c.WaId == waId).Id;

        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.PostAsync($"/api/contacts/{contactId}/suppression", null)).StatusCode);
        await _host.Services.GetRequiredService<MediaRetentionRunner>().RunOnceAsync();

        Assert.Null(await Store.OpenReadAsync(f.TenantId, key));
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MediaObjectDeletions.IgnoreQueryFilters().AnyAsync(d => d.TenantId == f.TenantId));
    }

    [Fact]
    public async Task DeletingTheCompanysData_DeletesEveryStoredObjectOfIt()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        await DownloadAllAsync();
        var key = (await MessageAsync(id)).MediaObjectKey!;
        var admin = await StartAdminAsync(_factory, _client);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status", new { status = "Cancelled", reason = "Fin del contrato" })).StatusCode);
        var name = (await GetAdminTenantAsync(admin, f.TenantId)).Name;
        Assert.Equal(HttpStatusCode.Accepted,
            (await admin.Client.PostAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/data-deletion", new { confirmName = name, reason = "Solicitud del cliente" })).StatusCode);

        var deleter = new Infrastructure.Tenants.TenantDataDeleter(_host.Services.GetRequiredService<IServiceScopeFactory>(),
            new Infrastructure.Tenants.TenantDeletionOptions(false, 60, 1000), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Tenants.TenantDataDeleter>.Instance);
        Assert.Equal(Infrastructure.Tenants.TenantDeletionRun.Completed, await deleter.DeleteAsync(f.TenantId));

        Assert.Null(await Store.OpenReadAsync(f.TenantId, key));
        Assert.Equal(0, await Store.DeleteAllAsync(f.TenantId));
    }

    [Fact]
    public async Task ASandboxAttachment_IsASyntheticFile_FromTheInternalClient()
    {
        var f = await StartChannelAsync(_factory, _client);
        var admin = await StartAdminAsync(_factory, _client);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/sandbox-mode", new { enabled = true, reason = "Pruebas" })).StatusCode);
        var owner = f.Tenant.Owner.Client;
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync("/api/sandbox/conversations", new { channelId = f.Channel.Id, customerName = "Laura", text = "Hola" })).StatusCode);
        await DrainAsync(RunnerOf(_host.Services));
        var conversation = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);
        Assert.Equal(HttpStatusCode.Accepted,
            (await owner.PostAsJsonAsync($"/api/sandbox/conversations/{conversation.Id}/customer-messages", new { attachment = "document", text = "Mi cotización" })).StatusCode);
        await DrainAsync(RunnerOf(_host.Services));

        await DownloadAllAsync();

        var document = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Single(m => m.Type == "document");
        Assert.Equal((MediaStatus.Stored, "application/pdf"), (document.MediaStatus!.Value, document.MediaContentType));
        Assert.Equal("Mi cotización", document.Text);

        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync("/api/sandbox/data")).StatusCode);
        await _host.Services.GetRequiredService<MediaRetentionRunner>().RunOnceAsync();
        Assert.Null(await Store.OpenReadAsync(f.TenantId, document.MediaObjectKey!));
    }

    [Fact]
    public async Task AStoreOutage_KeepsTheMediaPending_BackedOff_WithoutUsingUpItsAttempts()
    {
        var (f, id, mediaId) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        var down = new DownStore();
        var runner = ActivatorUtilities.CreateInstance<MediaDownloadRunner>(_host.Services, (IMediaStore)down);

        for (var round = 0; round < 5; round++)
        {
            for (var guard = 0; guard < 50 && await runner.RunOnceAsync(); guard++)
            {
            }

            await ExecuteAsync($"""UPDATE "Messages" SET "MediaNextAttemptAt" = now() - interval '1 second' WHERE "Id" = {id}""");
        }

        for (var guard = 0; guard < 50 && await runner.RunOnceAsync(); guard++)
        {
        }

        var message = await MessageAsync(id);
        Assert.Equal((MediaStatus.Pending, 0), (message.MediaStatus!.Value, message.MediaAttempts));
        Assert.True(down.Puts >= 6);
        Assert.True(message.MediaNextAttemptAt >= _host.Clock.GetUtcNow().UtcDateTime.AddMinutes(4), "backed off, not retried at once");
        _ = f;
        _ = mediaId;
    }

    [Fact]
    public async Task ARecordThatFails_AfterTheCopyWasStored_LeavesNoObjectBehind()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        var runner = ActivatorUtilities.CreateInstance<MediaDownloadRunner>(_host.Services);
        runner.BeforeRecordCommit = () => throw new InvalidOperationException("crash between the store and the record");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync());

        Assert.Equal(MediaStatus.Pending, (await MessageAsync(id)).MediaStatus);
        Assert.Empty(await ListAsync(f.TenantId));
    }

    private async Task<List<StoredObjectInfo>> ListAsync(Guid tenantId)
    {
        var all = new List<StoredObjectInfo>();
        await foreach (var item in Store.ListAsync(tenantId))
        {
            all.Add(item);
        }

        return all;
    }

    [Fact]
    public async Task TheOrphanSweep_DeletesObjectsNothingReferences_AndKeepsTheOthers()
    {
        var (f, id, _) = await ReceiveImageAsync(new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg"));
        await DownloadAllAsync();
        var kept = (await MessageAsync(id)).MediaObjectKey!;
        var orphan = await Store.PutAsync(f.TenantId, new MemoryStream(Jpeg), "image/jpeg"); // a process that died between the store and the record

        var swept = await _host.Services.GetRequiredService<MediaOrphanSweeper>().SweepAsync(olderThan: DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(swept >= 1);
        Assert.Null(await Store.OpenReadAsync(f.TenantId, orphan.ObjectKey));
        Assert.NotNull(await Store.OpenReadAsync(f.TenantId, kept));
    }

    [Fact]
    public async Task TheOrphanSweep_LeavesRecentObjects_ForARecordInFlight()
    {
        var f = await StartChannelAsync(_factory, _client);
        var recent = await Store.PutAsync(f.TenantId, new MemoryStream(Jpeg), "image/jpeg");

        await _host.Services.GetRequiredService<MediaOrphanSweeper>().SweepAsync(olderThan: DateTimeOffset.UtcNow.AddHours(-1));

        Assert.NotNull(await Store.OpenReadAsync(f.TenantId, recent.ObjectKey));
    }

    [Fact]
    public async Task TwoRunners_AtOnce_DownloadEachMediaOnce()
    {
        var f = await StartChannelAsync(_factory, _client);
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var mediaId = NewMediaId();
            ids.Add(mediaId);
            _host.Client.Answers[mediaId] = new MediaDownloadResult(MediaDownloadStatus.Downloaded, Jpeg, "image/jpeg");
            await DeliverAsync(_client, f, ImageDelivery($"wamid.PAR-{Guid.NewGuid():N}", mediaId));
        }

        await DrainAsync(RunnerOf(_host.Services));
        var a = ActivatorUtilities.CreateInstance<MediaDownloadRunner>(_host.Services);
        var b = ActivatorUtilities.CreateInstance<MediaDownloadRunner>(_host.Services);

        await Task.WhenAll(Loop(a), Loop(b));

        Assert.All(ids, mediaId => Assert.Single(_host.Client.Asked, asked => asked == mediaId));
        Assert.All((await SnapshotAsync(_factory.Services, f.TenantId)).Messages, m => Assert.Equal(MediaStatus.Stored, m.MediaStatus));

        static async Task Loop(MediaDownloadRunner runner)
        {
            for (var guard = 0; guard < 50 && await runner.RunOnceAsync(); guard++)
            {
            }
        }
    }

    private async Task QueueAsync(Guid tenantId, int count, string suffix = "")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.MediaObjectDeletions.Add(MediaObjectDeletion.Create(tenantId, $"{tenantId:N}/2026/11/{Guid.NewGuid():N}{suffix}", DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<MediaObjectDeletion>> QueuedAsync(Guid tenantId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MediaObjectDeletions.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId).ToListAsync();
    }

    [Fact]
    public async Task TheDeletionQueue_IsDrainedInBatchesUntilEmpty_InOneRun()
    {
        var f = await StartChannelAsync(_factory, _client);
        await QueueAsync(f.TenantId, 450);

        await _host.Services.GetRequiredService<MediaRetentionRunner>().DrainAsync(f.TenantId);

        Assert.Empty(await QueuedAsync(f.TenantId));
    }

    [Fact]
    public async Task AKeyTheStoreCannotDelete_IsRetriedLater_AndDoesNotBlockTheOthers()
    {
        var f = await StartChannelAsync(_factory, _client);
        await QueueAsync(f.TenantId, 1, suffix: "poison");
        await QueueAsync(f.TenantId, 3);
        var retention = ActivatorUtilities.CreateInstance<MediaRetentionRunner>(_host.Services, (IMediaStore)new DownStore());

        await retention.DrainAsync(f.TenantId);
        await retention.DrainAsync(f.TenantId);

        var left = Assert.Single(await QueuedAsync(f.TenantId));
        Assert.EndsWith("poison", left.ObjectKey);
        Assert.Equal(1, left.Attempts);
        Assert.True(left.NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task TheDeletionQueue_IsDrained_EvenWithTheRetentionOff()
    {
        var f = await StartChannelAsync(_factory, _client);
        await QueueAsync(f.TenantId, 2);
        var service = new MediaBackgroundService(
            _host.Services.GetRequiredService<MediaDownloadRunner>(), _host.Services.GetRequiredService<MediaRetentionRunner>(),
            _host.Services.GetRequiredService<Infrastructure.Configuration.MediaOptions>(), downloadEnabled: false, retentionEnabled: false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaBackgroundService>.Instance);

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await QueuedAsync(f.TenantId));
    }

    [Fact]
    public async Task TheMonitoring_CountsTheDeletionQueue_AndItsOldestAge()
    {
        var f = await StartChannelAsync(_factory, _client);
        await QueueAsync(f.TenantId, 2);
        var admin = await StartAdminAsync(_factory, _client);

        var monitoring = await admin.Client.GetFromJsonAsync<Application.DTOs.Admin.TenantMonitoringResponse>($"/api/admin/tenants/{f.TenantId}/monitoring");

        Assert.Equal(2, monitoring!.Backlog.FileDeletionsQueued);
        Assert.NotNull(monitoring.Backlog.OldestFileDeletionSeconds);
    }

    [Fact]
    public async Task WithoutAStore_TheDeletionQueueIsKept_ForWhenOneIsConfigured()
    {
        var f = await StartChannelAsync(_factory, _client);
        await QueueAsync(f.TenantId, 3);
        var none = _host.Services.GetRequiredService<Infrastructure.Configuration.MediaOptions>() with { Store = Infrastructure.Configuration.MediaStoreKind.None };
        var retention = ActivatorUtilities.CreateInstance<MediaRetentionRunner>(_host.Services, (IMediaStore)new Application.Media.UnavailableMediaStore(), none);

        await retention.DrainAsync(f.TenantId);

        Assert.Equal(3, (await QueuedAsync(f.TenantId)).Count);
    }
}
