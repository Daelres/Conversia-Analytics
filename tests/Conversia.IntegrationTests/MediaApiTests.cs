using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Messaging;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Serving stored media to the company's users and sending an attachment (docs/MESSAGING.md, "Media"): tenant isolation, permissions, the
/// headers (type checked, nosniff, private cache, attachment for documents), range requests, the send by media id and its refusals.
/// The provider is the fake media client of <see cref="MediaJobsTests"/>; the files are synthetic bytes.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MediaApiTests
{
    private static readonly byte[] Pdf = "%PDF-1.4\nsintetico\n%%EOF\n"u8.ToArray();
    private static readonly byte[] Exe = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MediaJobsTests.MediaHost _host;

    public MediaApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = factory.GetOrAddHost("media-jobs", () => MediaJobsTests.MediaHost.Create(factory));
    }

    private HttpClient ClientOf(string token)
    {
        var client = _host.Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A company whose customer just wrote (window open) and sent a document that is now stored.</summary>
    private async Task<(ChannelFixture Fixture, Guid ConversationId, Guid MessageId)> StoredDocumentAsync(byte[]? content = null)
    {
        var f = await StartChannelAsync(_factory, _client);
        await SetKeyAsync(f);
        var mediaId = "8" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);
        _host.Client.Answers[mediaId] = new MediaDownloadResult(MediaDownloadStatus.Downloaded, content ?? Pdf, "application/pdf");
        var providerId = $"wamid.DOC-{Guid.NewGuid():N}";
        var body = Dialog360Samples.InboundImage(providerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30)
            .Replace("\"type\": \"image\", \"image\": { \"id\": \"9999\", \"mime_type\": \"image/jpeg\"", $"\"type\": \"document\", \"document\": {{ \"id\": \"{mediaId}\", \"mime_type\": \"application/pdf\"", StringComparison.Ordinal);
        await DeliverAsync(_client, f, body);
        await DrainAsync(RunnerOf(_host.Services));
        var runner = _host.Services.GetRequiredService<Infrastructure.Media.MediaDownloadRunner>();
        for (var guard = 0; guard < 50 && await runner.RunOnceAsync(); guard++)
        {
        }

        var message = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Single(m => m.ProviderMessageId == providerId);
        Assert.Equal(MediaStatus.Stored, message.MediaStatus);
        return (f, message.ConversationId, message.Id);
    }

    private async Task SetKeyAsync(ChannelFixture f)
    {
        var response = await ClientOf(f.Tenant.Owner.Token).PutAsJsonAsync($"/api/channels/{f.Channel.Id}/provider-credentials", new { apiKey = $"d360-{Guid.NewGuid():N}", environment = "Production" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string MediaUrl(Guid conversationId, Guid messageId) => $"/api/conversations/{conversationId}/messages/{messageId}/media";

    [Fact]
    public async Task AStoredDocument_IsStreamedToItsCompany_AsAnAttachment_WithSafeHeaders()
    {
        var (f, conversationId, messageId) = await StoredDocumentAsync();

        using var response = await ClientOf(f.Tenant.Owner.Token).GetAsync(MediaUrl(conversationId, messageId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.DoesNotContain(".exe", response.Content.Headers.ContentDisposition.FileName ?? string.Empty);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.False(response.Headers.CacheControl.Public);
    }

    [Fact]
    public async Task ARangeRequest_GetsOnlyThatRange()
    {
        var (f, conversationId, messageId) = await StoredDocumentAsync();
        var client = ClientOf(f.Tenant.Owner.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, MediaUrl(conversationId, messageId));
        request.Headers.Range = new RangeHeaderValue(0, 3);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("%PDF"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnotherCompany_GetsA404_AsForAnUnknownMessage()
    {
        var (_, conversationId, messageId) = await StoredDocumentAsync();
        var stranger = await StartChannelAsync(_factory, _client);

        using var response = await ClientOf(stranger.Tenant.Owner.Token).GetAsync(MediaUrl(conversationId, messageId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AMessageOfAnotherConversation_IsA404()
    {
        var (f, _, messageId) = await StoredDocumentAsync();

        using var response = await ClientOf(f.Tenant.Owner.Token).GetAsync(MediaUrl(Guid.NewGuid(), messageId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AUserWithoutConversationsView_IsForbidden()
    {
        var (f, conversationId, messageId) = await StoredDocumentAsync();
        var member = await SeedMemberAsync(_factory, _client, f.TenantId, "media-nobody");

        using var response = await ClientOf(member.Token).GetAsync(MediaUrl(conversationId, messageId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheConversation_DescribesTheAttachment_WithoutTheProviderIdNorTheKey()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();

        var json = await ClientOf(f.Tenant.Owner.Token).GetStringAsync($"/api/conversations/{conversationId}");

        Assert.Contains("\"attachment\"", json);
        Assert.Contains("\"stored\"", json);
        Assert.DoesNotContain("mediaId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.TenantId.ToString("N"), json);
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType, string fileName, string? caption = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        if (caption is not null)
        {
            form.Add(new StringContent(caption), "caption");
        }

        return form;
    }

    [Fact]
    public async Task SendingADocument_UploadsIt_SendsItById_AndRecordsTheStoredCopy()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync(
            $"/api/conversations/{conversationId}/media-messages", Upload(Pdf, "application/pdf", "cotizacion-laura-3005550101.pdf", "Tu cotización"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await ReadBodyAsync<MessageResponse>(response);
        Assert.Equal(("Outbound", "document", "Tu cotización", "stored"), (sent.Direction, sent.Type, sent.Text, sent.Attachment!.Status));
        Assert.Contains(_host.Client.Sent, s => s.Type == "document" && s.Caption == "Tu cotización");
        var stored = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Single(m => m.Id == sent.Id);
        Assert.Equal((MessageSender.Conversia, MediaStatus.Stored), (stored.Sender, stored.MediaStatus!.Value));
        Assert.DoesNotContain("cotizacion", stored.MediaObjectKey!);
    }

    [Fact]
    public async Task AnExecutable_IsRefusedBeforeAnythingIsSent()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        var before = _host.Client.Sent.Count;

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync(
            $"/api/conversations/{conversationId}/media-messages", Upload(Exe, "application/pdf", "factura.pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("media_type_not_allowed", (await ReadProblemCodeAsync(response)).Code);
        Assert.Equal(before, _host.Client.Sent.Count);
    }

    [Fact]
    public async Task OutsideTheWindow_AnAttachmentIsRefused()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"""UPDATE "Conversations" SET "LastInboundAt" = now() - interval '25 hours' WHERE "Id" = {conversationId}""");
        }

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync(
            $"/api/conversations/{conversationId}/media-messages", Upload(MediaJobsTests.Jpeg, "image/jpeg", "foto.jpg"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("customer_service_window_closed", (await ReadProblemCodeAsync(response)).Code);
    }

    private async Task<int> ObjectCountAsync(Guid tenantId)
    {
        var count = 0;
        await foreach (var _ in _host.Services.GetRequiredService<Application.Media.IMediaStore>().ListAsync(tenantId))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task AnImageOverItsLimit_IsRefusedFromItsFirstBytes_BeforeAnythingIsStoredOrSent()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        var objects = await ObjectCountAsync(f.TenantId);
        var sent = _host.Client.Sent.Count;
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[5 * 1024 * 1024]];

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync($"/api/conversations/{conversationId}/media-messages", Upload(png, "image/png", "foto.png"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("media_too_large", (await ReadProblemCodeAsync(response)).Code);
        Assert.Equal(objects, await ObjectCountAsync(f.TenantId));
        Assert.Equal(sent, _host.Client.Sent.Count);
    }

    [Fact]
    public async Task AWordDocumentWithMacros_IsRefused()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        byte[] docx = [.. "PK\u0003\u0004"u8.ToArray(), .. new byte[26], .. "[Content_Types].xml"u8.ToArray(), .. new byte[10_000], .. "word/vbaProject.bin"u8.ToArray()];

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync($"/api/conversations/{conversationId}/media-messages",
            Upload(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "propuesta.docx"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task ASendThatBreaks_LeavesNoStoredCopyBehind()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        var objects = await ObjectCountAsync(f.TenantId);
        _host.Client.ThrowOnNextSend = true;

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync($"/api/conversations/{conversationId}/media-messages", Upload(Pdf, "application/pdf", "c.pdf"));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(objects, await ObjectCountAsync(f.TenantId));
    }

    [Fact]
    public async Task WhenTheEchoArrivesFirst_TheSendClaimsIt_AndItsStoredCopyIsKept_NotOrphaned()
    {
        var (f, conversationId, _) = await StoredDocumentAsync();
        var objects = await ObjectCountAsync(f.TenantId);
        var messageId = $"wamid.ECHOFIRST-{Guid.NewGuid():N}";
        _host.Client.NextMessageId = messageId;
        _host.Client.DuringSend = async id =>
        {
            await DeliverAsync(_client, f, Dialog360Samples.EchoImageDocShaped(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            await DrainAsync(RunnerOf(_host.Services));
        };

        using var response = await ClientOf(f.Tenant.Owner.Token).PostAsync($"/api/conversations/{conversationId}/media-messages",
            Upload(MediaJobsTests.Jpeg, "image/jpeg", "foto.jpg"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Single(m => m.ProviderMessageId == messageId);
        Assert.Equal((MessageSender.Conversia, MediaStatus.Stored), (message.Sender, message.MediaStatus!.Value));
        Assert.NotNull(await _host.Services.GetRequiredService<Application.Media.IMediaStore>().OpenReadAsync(f.TenantId, message.MediaObjectKey!));
        Assert.Equal(objects + 1, await ObjectCountAsync(f.TenantId));
    }

    [Fact]
    public async Task WithoutAMediaStore_AnAttachmentIsRefused_AndNothingIsServed()
    {
        var host = _factory.GetOrAddHost("media-none", () => WebhookHost(_factory, new Dictionary<string, string> { ["Media:Store"] = "None" }));
        var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.NONE-{Guid.NewGuid():N}", timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30));
        await DrainAsync(RunnerOf(host.Services));
        var conversation = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Tenant.Owner.Token);

        using var send = await client.PostAsync($"/api/conversations/{conversation.Id}/media-messages", Upload(Pdf, "application/pdf", "c.pdf"));
        using var file = await client.GetAsync(MediaUrl(conversation.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.StatusCode);
        Assert.Equal("media_storage_unavailable", (await ReadProblemCodeAsync(send)).Code);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }
}
