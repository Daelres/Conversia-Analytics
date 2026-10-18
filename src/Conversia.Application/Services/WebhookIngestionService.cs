using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class WebhookIngestionService : IWebhookIngestionService
{
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IWebhookEventRepository _events;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public WebhookIngestionService(
        IWhatsAppChannelRepository channels, IWebhookEventRepository events, ITenantContext tenantContext, TimeProvider timeProvider)
    {
        _channels = channels;
        _events = events;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    public async Task<WebhookChannelAccess?> AuthenticateAsync(string channelKey, string? secret, CancellationToken cancellationToken = default)
    {
        if (!WhatsAppChannel.IsWellFormedChannelKey(channelKey))
        {
            return null;
        }

        var channel = await _channels.GetByChannelKeyAsync(channelKey, cancellationToken);
        if (channel is null || !channel.IsActive || !channel.VerifyWebhookSecret(secret))
        {
            return null;
        }

        // The webhook has no JWT: the authenticated channel is what says whose data this request may touch.
        _tenantContext.SetTenant(channel.TenantId);
        return new WebhookChannelAccess(channel.Id, channel.TenantId);
    }

    public async Task<WebhookStoreResult> StoreAsync(WebhookChannelAccess channel, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        if (!IsStorableJsonObject(body.Span))
        {
            return WebhookStoreResult.InvalidPayload;
        }

        var webhookEvent = WebhookEvent.Receive(
            channel.TenantId,
            channel.ChannelId,
            SHA256.HashData(body.Span),
            Encoding.UTF8.GetString(body.Span),
            _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            return await _events.AddIfNewAsync(webhookEvent, cancellationToken) ? WebhookStoreResult.Stored : WebhookStoreResult.Duplicate;
        }
        catch (InvalidWebhookPayloadException)
        {
            return WebhookStoreResult.InvalidPayload;
        }
    }

    // Validation only: the body is a well-formed JSON object whose strings PostgreSQL jsonb can store. Nothing is read out of it here.
    // Raw (unescaped) characters cannot be a problem; an escape can: \u0000, or a surrogate escape without its pair (\ud800). They are found by
    // reading the escaped strings, so a customer typing the characters "\u0000" (an escaped backslash) is not mistaken for one.
    private static bool IsStorableJsonObject(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var depth = 1;
            while (depth > 0 && reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.String or JsonTokenType.PropertyName when reader.ValueIsEscaped && !IsStorableText(reader):
                        return false;
                }
            }

            return depth == 0 && !reader.Read(); // nothing but whitespace after the object
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStorableText(Utf8JsonReader reader)
    {
        string text;
        try
        {
            text = reader.GetString()!;
        }
        catch (InvalidOperationException)
        {
            return false; // the escapes do not form valid UTF-16
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0' || char.IsLowSurrogate(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
        }

        return true;
    }
}
