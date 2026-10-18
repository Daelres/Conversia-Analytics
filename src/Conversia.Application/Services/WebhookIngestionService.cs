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

    // Validation only: the body is a well-formed JSON object. Nothing is read out of it here. What well-formed JSON can still hold
    // and PostgreSQL jsonb refuses (a \u0000 escape, an unpaired surrogate) is refused by the insert and reported as InvalidPayload.
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

            reader.Skip();
            return !reader.Read(); // nothing but whitespace after the object
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
