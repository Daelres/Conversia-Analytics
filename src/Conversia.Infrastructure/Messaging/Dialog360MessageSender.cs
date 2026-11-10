using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// Sends through the 360dialog Cloud API: <c>POST {base}/v1/messages</c> with the channel's key in <c>D360-API-KEY</c>. The only class
/// that knows the shape of that call. One attempt per message: a POST is not idempotent, and after a timeout nobody knows whether the
/// message left. The key, the recipient, the text and the provider's bodies never reach a log; a refusal is logged by its status.
/// </summary>
public sealed class Dialog360MessageSender : IWhatsAppMessageSender
{
    public const string ApiKeyHeader = "D360-API-KEY";

    /// <summary>More than any answer to a send should weigh; a bigger one is not read.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// How long the body of an accepted message is waited for. Deliberately short and independent of the caller: the id in it is
    /// useful, the message having been sent is what matters.
    /// </summary>
    internal TimeSpan BodyReadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _http;
    private readonly ISecretProtector _secretProtector;
    private readonly Dialog360ApiOptions _options;
    private readonly ILogger<Dialog360MessageSender> _logger;

    public Dialog360MessageSender(HttpClient http, ISecretProtector secretProtector, Dialog360ApiOptions options, ILogger<Dialog360MessageSender> logger)
    {
        _http = http;
        _secretProtector = secretProtector;
        _options = options;
        _logger = logger;
    }

    public async Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
    {
        var channel = message.Channel;
        if (channel.ProviderApiKeyCiphertext is not { } ciphertext)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        // Throws SecretProtectionException when the key cannot be read; nothing has been sent then.
        var apiKey = _secretProtector.Unprotect(ciphertext, ProviderCredentialPurpose.ForChannel(channel.Id));

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(_options.BaseUrlOf(channel.ProviderEnvironment)))
        {
            Content = new ByteArrayContent(TextBody(message)) { Headers = { ContentType = JsonMediaType } },
        };
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);

        HttpResponseMessage response;
        try
        {
            // Up to the status line the caller may still give up: nothing is known to have been accepted.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // The type only: the message of a transport exception can name hosts and, from a handler, anything it was given.
            _logger.LogWarning(
                "The call to 360dialog for a message of channel {ChannelId} ({Environment}) failed: {ExceptionType}.",
                channel.Id, channel.ProviderEnvironment, ex.GetType().Name);
            return WhatsAppSendResult.Failed(WhatsAppSendStatus.TransientFailure);
        }

        using (response)
        {
            var status = StatusOf(response.StatusCode);
            if (status != WhatsAppSendStatus.Sent)
            {
                // The body is deliberately not read: it can repeat the request, and nothing in it changes what the caller is told.
                _logger.LogWarning(
                    "360dialog refused a message of channel {ChannelId} ({Environment}) with HTTP {StatusCode}: {Outcome}.",
                    channel.Id, channel.ProviderEnvironment, (int)response.StatusCode, status);
                return WhatsAppSendResult.Failed(status);
            }

            // From here on the message has left. Whatever happens to the body (slow, broken, the caller gone), the answer is Sent, so
            // the caller of this port goes on to record it: the read has its own short time limit and ignores the caller's token.
            var providerMessageId = await ReadMessageIdAsync(response);
            if (providerMessageId is null)
            {
                _logger.LogWarning(
                    "360dialog accepted a message of channel {ChannelId} with HTTP {StatusCode} but its answer carried no usable message id.",
                    channel.Id, (int)response.StatusCode);
            }

            return WhatsAppSendResult.Sent(providerMessageId);
        }
    }

    private static Uri MessagesUrl(Uri baseUrl)
        => new(baseUrl.AbsoluteUri.TrimEnd('/') + "/v1/messages", UriKind.Absolute);

    private static byte[] TextBody(WhatsAppTextMessage message)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("messaging_product", "whatsapp");
            json.WriteString("recipient_type", "individual");
            json.WriteString("to", message.RecipientWaId);
            json.WriteString("type", "text");
            json.WriteStartObject("text");
            json.WriteString("body", message.Text);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // 401, 403 and 429 are the provider's documented refusals; 408 and 5xx may pass; a redirect is never followed (the key would travel
    // with it), so like anything else unexpected it is "try later"; every other 4xx will fail again with the same message.
    private static WhatsAppSendStatus StatusOf(HttpStatusCode statusCode) => (int)statusCode switch
    {
        >= 200 and < 300 => WhatsAppSendStatus.Sent,
        401 => WhatsAppSendStatus.InvalidCredentials,
        403 => WhatsAppSendStatus.RecipientNotAllowed,
        429 => WhatsAppSendStatus.LimitReached,
        408 => WhatsAppSendStatus.TransientFailure,
        >= 400 and < 500 => WhatsAppSendStatus.Rejected,
        _ => WhatsAppSendStatus.TransientFailure,
    };

    // messages[0].id of the Cloud API answer. The message was accepted whatever this finds, so nothing here may throw.
    private async Task<string?> ReadMessageIdAsync(HttpResponseMessage response)
    {
        using var limit = new CancellationTokenSource(BodyReadTimeout);
        try
        {
            var bytes = await ReadAtMostAsync(response.Content, MaxResponseBytes, limit.Token);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0
                || messages[0].ValueKind != JsonValueKind.Object
                || !messages[0].TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = id.GetString()?.Trim();
            return string.IsNullOrEmpty(value) || value.Length > Message.ProviderMessageIdMaxLength || value.Contains('\0') ? null : value;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or DecoderFallbackException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
