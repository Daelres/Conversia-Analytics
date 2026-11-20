using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The media calls of 360dialog (docs/MESSAGING.md, "Verified provider facts"; https://docs.360dialog.com/docs/waba-messaging/media/upload-retrieve-or-delete-media):
/// <list type="bullet">
/// <item>download: <c>GET {base}/{media-id}</c> answers a <c>url</c> on <c>lookaside.fbsbx.com</c>; the file is fetched from
/// <c>{base}</c> + that URL's path and query, with the key. The lookaside host itself is never called (it would receive the key), and any other
/// host is refused. The link lasts 5 minutes, so the two calls follow each other;</item>
/// <item>upload: <c>POST {base}/media</c>, multipart <c>messaging_product=whatsapp</c> and <c>file</c> (a generic file name, never the person's);</item>
/// <item>send: the Cloud API media message by id on the same path as the text sender (<c>{base}/v1/messages</c>).</item>
/// </list>
/// The key, the URL, the number and the bodies never reach a log.
/// </summary>
public sealed class Dialog360MediaClient(
    HttpClient http, ISecretProtector protector, Dialog360ApiOptions options, ILogger<Dialog360MediaClient> logger, TimeSpan? downloadTimeout = null) : IWhatsAppMediaClient
{
    /// <summary>The whole of one download (both calls and the body), so a stalled transfer never holds the job: <c>Media:DownloadTimeoutSeconds</c>.</summary>
    private readonly TimeSpan _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(2);

    /// <summary>The host of the URLs <c>GET /{media-id}</c> answers (360dialog's page).</summary>
    public const string LookasideHost = "lookaside.fbsbx.com";

    private const int MaxJsonBytes = 64 * 1024;

    public async Task<MediaDownloadResult> DownloadAsync(
        WhatsAppChannel channel, string providerMediaId, long maxBytes, string? declaredMimeType = null, CancellationToken cancellationToken = default)
    {
        if (!IsMediaId(providerMediaId))
        {
            return MediaDownloadResult.Of(MediaDownloadStatus.Rejected);
        }

        if (ApiKeyOf(channel) is not { } apiKey)
        {
            return MediaDownloadResult.Of(MediaDownloadStatus.CredentialsUnavailable);
        }

        var baseUrl = options.BaseUrlOf(channel.ProviderEnvironment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadTimeout);
        var token = deadline.Token;
        try
        {
            using var metadataRequest = Request(HttpMethod.Get, new Uri(baseUrl, "/" + providerMediaId), apiKey);
            using var metadata = await http.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (OutcomeOf(metadata.StatusCode) is { } failure)
            {
                logger.LogWarning("360dialog answered HTTP {StatusCode} to a media lookup of channel {ChannelId}.", (int)metadata.StatusCode, channel.Id);
                return MediaDownloadResult.Of(failure);
            }

            var json = await Dialog360MessageSender.ReadAtMostAsync(metadata.Content, MaxJsonBytes, token);
            if (json is null || ReadMetadata(json) is not { } info)
            {
                return MediaDownloadResult.Of(MediaDownloadStatus.Rejected);
            }

            if (info.FileSize is { } announced && announced > maxBytes)
            {
                return MediaDownloadResult.Of(MediaDownloadStatus.TooLarge);
            }

            if (DownloadUrl(info.Url, baseUrl) is not { } downloadUrl)
            {
                logger.LogWarning("360dialog answered a media URL of an unexpected shape for channel {ChannelId}; it was not fetched.", channel.Id);
                return MediaDownloadResult.Of(MediaDownloadStatus.Rejected);
            }

            using var fileRequest = Request(HttpMethod.Get, downloadUrl, apiKey);
            using var file = await http.SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (OutcomeOf(file.StatusCode) is { } fileFailure)
            {
                logger.LogWarning("360dialog answered HTTP {StatusCode} to a media download of channel {ChannelId}.", (int)file.StatusCode, channel.Id);
                return MediaDownloadResult.Of(fileFailure);
            }

            if (file.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                return MediaDownloadResult.Of(MediaDownloadStatus.TooLarge);
            }

            var spooled = await SpoolAtMostAsync(file.Content, maxBytes, token);
            return spooled is null
                ? MediaDownloadResult.Of(MediaDownloadStatus.TooLarge)
                : new MediaDownloadResult(MediaDownloadStatus.Downloaded, spooled, info.MimeType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogWarning("A media download of channel {ChannelId} failed: {ExceptionType}.", channel.Id, ex.GetType().Name);
            return MediaDownloadResult.Of(MediaDownloadStatus.TransientFailure);
        }
    }

    public async Task<MediaUploadResult> UploadAsync(WhatsAppChannel channel, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (ApiKeyOf(channel) is not { } apiKey)
        {
            return new MediaUploadResult(WhatsAppSendStatus.InvalidCredentials, null);
        }

        var file = new StreamContent(content); // streamed from the caller's file, not copied into memory
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("whatsapp"), "messaging_product" },
            { file, "file", "file" },
        };
        using var request = Request(HttpMethod.Post, new Uri(options.BaseUrlOf(channel.ProviderEnvironment), "/media"), apiKey);
        request.Content = form;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = SendStatusOf(response.StatusCode);
            if (status != WhatsAppSendStatus.Sent)
            {
                logger.LogWarning("360dialog refused a media upload of channel {ChannelId} with HTTP {StatusCode}.", channel.Id, (int)response.StatusCode);
                return new MediaUploadResult(status, null);
            }

            var json = await Dialog360MessageSender.ReadAtMostAsync(response.Content, MaxJsonBytes, cancellationToken);
            var id = json is null ? null : ReadString(json, "id");
            return IsMediaId(id) ? new MediaUploadResult(WhatsAppSendStatus.Sent, id) : new MediaUploadResult(WhatsAppSendStatus.Rejected, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            logger.LogWarning("A media upload of channel {ChannelId} failed: {ExceptionType}.", channel.Id, ex.GetType().Name);
            return new MediaUploadResult(WhatsAppSendStatus.TransientFailure, null);
        }
    }

    public async Task<WhatsAppSendResult> SendMediaAsync(WhatsAppMediaMessage message, CancellationToken cancellationToken = default)
    {
        if (ApiKeyOf(message.Channel) is not { } apiKey)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        using var request = Request(HttpMethod.Post, new Uri(options.BaseUrlOf(message.Channel.ProviderEnvironment).AbsoluteUri.TrimEnd('/') + "/v1/messages"), apiKey);
        request.Content = new ByteArrayContent(MediaBody(message)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = SendStatusOf(response.StatusCode);
            if (status != WhatsAppSendStatus.Sent)
            {
                logger.LogWarning("360dialog refused a media message of channel {ChannelId} with HTTP {StatusCode}.", message.Channel.Id, (int)response.StatusCode);
                return WhatsAppSendResult.Failed(status);
            }

            // Sent from here on: the id is useful, not required.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var json = await Dialog360MessageSender.ReadAtMostAsync(response.Content, MaxJsonBytes, limit.Token);
                return WhatsAppSendResult.Sent(json is null ? null : ReadMessageId(json));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                return WhatsAppSendResult.Sent(null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogWarning("A media message of channel {ChannelId} failed: {ExceptionType}.", message.Channel.Id, ex.GetType().Name);
            return WhatsAppSendResult.Failed(WhatsAppSendStatus.TransientFailure);
        }
    }

    private const string LookasidePrefix = "https://" + LookasideHost + "/";

    /// <summary>
    /// The 360dialog address of a lookaside URL: its path and query on <paramref name="baseUrl"/>, or null for anything that is not plainly a
    /// lookaside URL. The key goes with the request, so the rules are strict: exactly <c>https://lookaside.fbsbx.com/</c> (no user info, no port),
    /// a path that does not start with <c>//</c> and has no backslash, no dot segment and no encoded dot, slash or backslash; and the address built
    /// must be on the base's own host and port.
    /// </summary>
    internal static Uri? DownloadUrl(string? raw, Uri baseUrl)
    {
        if (raw is null || raw.Length > 4096 || !raw.StartsWith(LookasidePrefix, StringComparison.OrdinalIgnoreCase) || raw.Any(char.IsControl))
        {
            return null;
        }

        var rest = raw[(LookasidePrefix.Length - 1)..]; // from the first slash of the path
        var queryStart = rest.IndexOf('?');
        var path = queryStart < 0 ? rest : rest[..queryStart];
        var query = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];
        if (path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || query.Contains('#')
            || path.Split('/').Any(segment => segment is "." or "..")
            || path.Contains("%2e", StringComparison.OrdinalIgnoreCase) || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase) || query.Contains('\\'))
        {
            return null;
        }

        var built = new UriBuilder(baseUrl) { Path = path, Query = query }.Uri;
        return string.Equals(built.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) && built.Port == baseUrl.Port && built.Scheme == baseUrl.Scheme
            ? built
            : null;
    }

    // A provider media id is digits (Meta's ids); anything else never goes into a path.
    private static bool IsMediaId(string? id) => id is { Length: >= 1 and <= MessageMedia.MaxIdLength } && id.All(char.IsAsciiLetterOrDigit);

    private string? ApiKeyOf(WhatsAppChannel channel)
    {
        if (channel.ProviderApiKeyCiphertext is not { } ciphertext)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(ciphertext, ProviderCredentialPurpose.ForChannel(channel.Id));
        }
        catch (SecretProtectionException)
        {
            return null;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(Dialog360MessageSender.ApiKeyHeader, apiKey);
        return request;
    }

    private static MediaDownloadStatus? OutcomeOf(HttpStatusCode code) => (int)code switch
    {
        >= 200 and < 300 => null,
        401 or 403 => MediaDownloadStatus.CredentialsUnavailable,
        404 or 410 => MediaDownloadStatus.NotFound,
        408 or 429 => MediaDownloadStatus.TransientFailure,
        >= 400 and < 500 => MediaDownloadStatus.Rejected,
        _ => MediaDownloadStatus.TransientFailure,
    };

    private static WhatsAppSendStatus SendStatusOf(HttpStatusCode code) => (int)code switch
    {
        >= 200 and < 300 => WhatsAppSendStatus.Sent,
        401 => WhatsAppSendStatus.InvalidCredentials,
        403 => WhatsAppSendStatus.RecipientNotAllowed,
        429 => WhatsAppSendStatus.LimitReached,
        408 => WhatsAppSendStatus.TransientFailure,
        >= 400 and < 500 => WhatsAppSendStatus.Rejected,
        _ => WhatsAppSendStatus.TransientFailure,
    };

    private sealed record MediaInfo(string? Url, string? MimeType, long? FileSize);

    private static MediaInfo? ReadMetadata(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            long? size = root.TryGetProperty("file_size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : null;
            return new MediaInfo(ReadString(root, "url"), ReadString(root, "mime_type"), size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(byte[] json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? ReadString(document.RootElement, name) : null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadMessageId(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            && messages.GetArrayLength() > 0 && messages[0].ValueKind == JsonValueKind.Object && ReadString(messages[0], "id") is { } id)
        {
            id = id.Trim();
            return id.Length is > 0 and <= Message.ProviderMessageIdMaxLength && !id.Contains('\0') ? id : null;
        }

        return null;
    }

    private static byte[] MediaBody(WhatsAppMediaMessage message)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("messaging_product", "whatsapp");
            json.WriteString("recipient_type", "individual");
            json.WriteString("to", message.RecipientWaId);
            json.WriteString("type", message.Type);
            json.WriteStartObject(message.Type);
            json.WriteString("id", message.ProviderMediaId);
            if (!string.IsNullOrEmpty(message.Caption))
            {
                json.WriteString("caption", message.Caption);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // The body into a temporary file that deletes itself when closed: a 100 MB document never sits in memory. Null past the limit.
    private static async Task<FileStream?> SpoolAtMostAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var spool = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (spool.Length + read > maxBytes)
                {
                    await spool.DisposeAsync();
                    return null;
                }

                await spool.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }
}
