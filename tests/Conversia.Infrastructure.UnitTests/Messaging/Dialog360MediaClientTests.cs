using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Infrastructure.UnitTests.Messaging;

/// <summary>
/// The 360dialog media calls against a fake transport (docs/MESSAGING.md, "Verified provider facts"): <c>GET /{media-id}</c>, the download
/// through 360dialog's host (never the lookaside host, which would receive the key), <c>POST /media</c> and the media message by id.
/// </summary>
public class Dialog360MediaClientTests
{
    private const string ApiKey = "d360-key-for-tests-Ab3";
    private static readonly byte[] File = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private readonly Handler _handler = new();

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Key, string? ContentType, byte[] Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.TryGetValues("D360-API-KEY", out var v) ? v.Single() : null,
                request.Content?.Headers.ContentType?.MediaType, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return Respond(request);
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public bool IsConfigured => true;

        public string Protect(string plaintext, string purpose) => plaintext;

        public string Unprotect(string protectedValue, string purpose) => protectedValue;
    }

    private Dialog360MediaClient Client(TimeSpan? downloadTimeout = null)
        => new(new HttpClient(_handler), new PlainProtector(), Dialog360ApiOptions.Default, NullLogger<Dialog360MediaClient>.Instance, downloadTimeout);

    /// <summary>A body that sends a few bytes and then stalls, like a transfer that hangs.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 0xFF;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static async Task<byte[]> BytesOf(MediaDownloadResult result)
    {
        using var copy = new MemoryStream();
        await result.Content!.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static WhatsAppChannel Channel(bool withKey = true)
    {
        var channel = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100").Channel;
        if (withKey)
        {
            channel.SetProviderCredentials(ApiKey, WhatsAppProviderEnvironment.Production, DateTime.UtcNow);
        }

        return channel;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Metadata(string url = "https:\\/\\/lookaside.fbsbx.com\\/whatsapp_business\\/attachments\\/?mid=123&ext=1792324800&hash=abc", long size = 8)
        => $$"""{"url":"{{url}}","mime_type":"image/jpeg","sha256":"x","file_size":{{size}},"id":"123","messaging_product":"whatsapp"}""";

    private void AnswerMetadataThenFile(string? metadata = null)
        => _handler.Respond = request => request.RequestUri!.AbsolutePath == "/123"
            ? Json(metadata ?? Metadata())
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File) };

    [Fact]
    public async Task ADownload_ReadsTheUrl_ThenFetchesItThrough360dialogsHost_WithTheKey()
    {
        AnswerMetadataThenFile();

        var result = await Client().DownloadAsync(Channel(), "123", 1000);

        Assert.Equal(MediaDownloadStatus.Downloaded, result.Status);
        Assert.Equal(File, await BytesOf(result));
        Assert.Equal("image/jpeg", result.DeclaredMimeType);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("https://waba-v2.360dialog.io/123", _handler.Requests[0].Uri.AbsoluteUri);
        var download = _handler.Requests[1].Uri;
        Assert.Equal("waba-v2.360dialog.io", download.Host);
        Assert.Equal("/whatsapp_business/attachments/", download.AbsolutePath);
        Assert.Contains("mid=123", download.Query);
        Assert.All(_handler.Requests, r => Assert.Equal(ApiKey, r.Key));
    }

    [Fact]
    public async Task AFileAnnouncedBiggerThanTheLimit_IsNotDownloaded()
    {
        AnswerMetadataThenFile(Metadata(size: 5_000));

        var result = await Client().DownloadAsync(Channel(), "123", 1000);

        Assert.Equal(MediaDownloadStatus.TooLarge, result.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task AFileBiggerThanTheLimitOnTheWire_IsTooLarge()
    {
        AnswerMetadataThenFile(Metadata(size: 1));

        var result = await Client().DownloadAsync(Channel(), "123", 4);

        Assert.Equal(MediaDownloadStatus.TooLarge, result.Status);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task AStalledTransfer_IsGivenUpAfterTheDeadline_AndRetriedLater()
    {
        _handler.Respond = request => request.RequestUri!.AbsolutePath == "/123"
            ? Json(Metadata())
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Client(TimeSpan.FromMilliseconds(300)).DownloadAsync(Channel(), "123", 1000);

        Assert.Equal(MediaDownloadStatus.TransientFailure, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task TheDownloadIsKeptOnDisk_NotInMemory()
    {
        AnswerMetadataThenFile();

        await using var result = await Client().DownloadAsync(Channel(), "123", 1000);

        Assert.IsType<FileStream>(result.Content);
        Assert.Equal(0, result.Content!.Position);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=1")]
    [InlineData("not a url")]
    [InlineData("https://lookaside.fbsbx.com//evil.example/x")] // a scheme-relative path would resolve to another host
    [InlineData("https://lookaside.fbsbx.com/a/../../x")]
    [InlineData("https://lookaside.fbsbx.com/a/%2e%2e/x")]
    [InlineData("https://user:pw@lookaside.fbsbx.com/whatsapp_business/attachments/?mid=1")]
    [InlineData("https://lookaside.fbsbx.com:8443/whatsapp_business/attachments/?mid=1")]
    [InlineData("https://lookaside.fbsbx.com/a\\\\b")]
    public async Task AUrlThatIsNotTheLookasideHost_IsNeverFetched(string url)
    {
        AnswerMetadataThenFile(Metadata(url: url));

        var result = await Client().DownloadAsync(Channel(), "123", 1000);

        Assert.Equal(MediaDownloadStatus.Rejected, result.Status);
        Assert.Single(_handler.Requests);
        Assert.All(_handler.Requests, r => Assert.Equal("waba-v2.360dialog.io", r.Uri.Host)); // the key never goes anywhere else
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, MediaDownloadStatus.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, MediaDownloadStatus.CredentialsUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, MediaDownloadStatus.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, MediaDownloadStatus.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, MediaDownloadStatus.Rejected)]
    public async Task EachAnswerOfTheMetadataCall_HasItsOutcome(HttpStatusCode status, MediaDownloadStatus expected)
    {
        _handler.Respond = _ => new HttpResponseMessage(status);

        Assert.Equal(expected, (await Client().DownloadAsync(Channel(), "123", 1000)).Status);
    }

    [Fact]
    public async Task AChannelWithoutKey_IsNotCalled()
    {
        var result = await Client().DownloadAsync(Channel(withKey: false), "123", 1000);

        Assert.Equal(MediaDownloadStatus.CredentialsUnavailable, result.Status);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("../v1/messages")]
    [InlineData("123?x=1")]
    [InlineData("")]
    public async Task AMediaIdThatIsNotAnId_IsNeverPutInAPath(string id)
    {
        Assert.Equal(MediaDownloadStatus.Rejected, (await Client().DownloadAsync(Channel(), id, 1000)).Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AnUpload_IsAMultipartPostToMedia_AndReturnsTheId()
    {
        _handler.Respond = _ => Json("""{"id":"1094408688491179"}""");

        var result = await Client().UploadAsync(Channel(), new MemoryStream(File), "image/jpeg");

        Assert.Equal((WhatsAppSendStatus.Sent, "1094408688491179"), (result.Status, result.ProviderMediaId));
        var request = Assert.Single(_handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://waba-v2.360dialog.io/media", "multipart/form-data"), (request.Method, request.Uri.AbsoluteUri, request.ContentType));
        var body = Encoding.Latin1.GetString(request.Body);
        Assert.Contains("name=messaging_product", body);
        Assert.Contains("whatsapp", body);
        Assert.Contains("name=file", body);
        Assert.Contains("Content-Type: image/jpeg", body);
        Assert.Contains("filename=file", body.Replace("\"", string.Empty)); // a generic name: the person's file name never leaves
    }

    [Fact]
    public async Task ASendByMediaId_PostsTheCloudApiMediaMessage_WithItsCaption()
    {
        _handler.Respond = _ => Json("""{"messages":[{"id":"wamid.MEDIA-1"}]}""", HttpStatusCode.Created);

        var result = await Client().SendMediaAsync(new WhatsAppMediaMessage(Channel(), "573005550101", "document", "998877", "Cotización"));

        Assert.Equal((WhatsAppSendStatus.Sent, "wamid.MEDIA-1"), (result.Status, result.ProviderMessageId));
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://waba-v2.360dialog.io/v1/messages", request.Uri.AbsoluteUri);
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal(("whatsapp", "individual", "573005550101", "document"),
            (root.GetProperty("messaging_product").GetString(), root.GetProperty("recipient_type").GetString(), root.GetProperty("to").GetString(), root.GetProperty("type").GetString()));
        Assert.Equal("998877", root.GetProperty("document").GetProperty("id").GetString());
        Assert.Equal("Cotización", root.GetProperty("document").GetProperty("caption").GetString());
        Assert.False(root.GetProperty("document").TryGetProperty("link", out _));
    }
}
