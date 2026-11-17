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

    public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
        => PostAsync(message.Channel, TextBody(message), readMessageId: true, cancellationToken);

    public Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken = default)
        => PostAsync(message.Channel, TemplateBody(message), readMessageId: true, cancellationToken);

    public async Task<WhatsAppSendStatus> MarkReadAsync(WhatsAppReadReceipt receipt, CancellationToken cancellationToken = default)
        => (await PostAsync(receipt.Channel, ReadBody(receipt), readMessageId: false, cancellationToken)).Status;

    // The one call every request of this adapter makes: POST {base}/v1/messages with the channel's key.
    private async Task<WhatsAppSendResult> PostAsync(WhatsAppChannel channel, byte[] body, bool readMessageId, CancellationToken cancellationToken)
    {
        if (channel.ProviderApiKeyCiphertext is not { } ciphertext)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        // Throws SecretProtectionException when the key cannot be read; nothing has been sent then.
        var apiKey = _secretProtector.Unprotect(ciphertext, ProviderCredentialPurpose.ForChannel(channel.Id));

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(_options.BaseUrlOf(channel.ProviderEnvironment)))
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = JsonMediaType } },
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
                // A generic refusal may be WhatsApp's "outside the 24-hour window" (Meta's error code 131047): only the numeric code of
                // the body is read, for a short time and a bounded size. The rest of the body is never read into a log: it can repeat the
                // request (the number, the text).
                int? metaCode = null;
                if (status == WhatsAppSendStatus.Rejected)
                {
                    metaCode = await ReadErrorCodeAsync(response);
                    if (metaCode == WindowClosedErrorCode)
                    {
                        status = WhatsAppSendStatus.WindowClosed;
                    }
                }

                _logger.LogWarning(
                    "360dialog refused a message of channel {ChannelId} ({Environment}) with HTTP {StatusCode} (error code {MetaCode}): {Outcome}.",
                    channel.Id, channel.ProviderEnvironment, (int)response.StatusCode, metaCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", status);
                return WhatsAppSendResult.Failed(status);
            }

            if (!readMessageId)
            {
                return WhatsAppSendResult.Sent(null);
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

    /// <summary>Meta's error code for a free-form message outside the customer service window (docs/MESSAGING.md, "Verified provider facts").</summary>
    public const int WindowClosedErrorCode = 131047;

    /// <summary>What is read of a refusal to find its error code.</summary>
    private const int MaxErrorBytes = 16 * 1024;

    // error.code of the Cloud API's error body; null when there is none or it cannot be read in time.
    private async Task<int?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var limit = new CancellationTokenSource(BodyReadTimeout);
        try
        {
            var bytes = await ReadAtMostAsync(response.Content, MaxErrorBytes, limit.Token);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.Number
                   && code.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or DecoderFallbackException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static Uri MessagesUrl(Uri baseUrl)
        => new(baseUrl.AbsoluteUri.TrimEnd('/') + "/v1/messages", UriKind.Absolute);

    // The Cloud API template message (docs/MESSAGING.md, "Verified provider facts"): name, language and the body's values in order; a
    // named placeholder carries its name. The language policy "deterministic" is the one 360dialog's reference lists.
    private static byte[] TemplateBody(WhatsAppTemplateMessage message)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("messaging_product", "whatsapp");
            json.WriteString("recipient_type", "individual");
            json.WriteString("to", message.RecipientWaId);
            json.WriteString("type", "template");
            json.WriteStartObject("template");
            json.WriteString("name", message.Name);
            json.WriteStartObject("language");
            json.WriteString("policy", "deterministic");
            json.WriteString("code", message.Language);
            json.WriteEndObject();
            if (message.Parameters.Count > 0)
            {
                json.WriteStartArray("components");
                json.WriteStartObject();
                json.WriteString("type", "body");
                json.WriteStartArray("parameters");
                foreach (var parameter in message.Parameters)
                {
                    json.WriteStartObject();
                    json.WriteString("type", "text");
                    if (parameter.Name is { } name)
                    {
                        json.WriteString("parameter_name", name);
                    }

                    json.WriteString("text", parameter.Value);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // Mark as read, and the typing indicator (which marks as read too): 360dialog's "Typing Indicators" page and Meta's "Mark messages as read".
    private static byte[] ReadBody(WhatsAppReadReceipt receipt)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("messaging_product", "whatsapp");
            json.WriteString("status", "read");
            json.WriteString("message_id", receipt.ProviderMessageId);
            if (receipt.Typing)
            {
                json.WriteStartObject("typing_indicator");
                json.WriteString("type", "text");
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

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

    internal static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
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
