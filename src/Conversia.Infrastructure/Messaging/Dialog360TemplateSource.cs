using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// Reads a channel's templates from 360dialog: <c>GET {base}/v1/configs/templates?limit=&amp;offset=</c> with the channel's key in
/// <c>D360-API-KEY</c>, page after page (docs/MESSAGING.md, "Verified provider facts"). The only class that knows that call. It keeps
/// what Conversia offers (name, language, category, status, body text) and decides whether Conversia can send the template; nothing of
/// the answer reaches a log but counts and statuses.
/// </summary>
public sealed class Dialog360TemplateSource : IWhatsAppTemplateSource
{
    /// <summary>The page size asked for (360dialog's default and documented value).</summary>
    public const int PageSize = 1000;

    /// <summary>A WhatsApp Business Account holds at most 250 template names, each in several languages: more pages than this is a defect.</summary>
    public const int MaxPages = 10;

    /// <summary>The largest page read; a template list is text, and 1000 templates of 1 KB fit.</summary>
    public const int MaxPageBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ISecretProtector _secretProtector;
    private readonly Dialog360ApiOptions _options;
    private readonly ILogger<Dialog360TemplateSource> _logger;

    public Dialog360TemplateSource(HttpClient http, ISecretProtector secretProtector, Dialog360ApiOptions options, ILogger<Dialog360TemplateSource> logger)
    {
        _http = http;
        _secretProtector = secretProtector;
        _options = options;
        _logger = logger;
    }

    public async Task<TemplateListResult> ListAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default)
    {
        if (channel.ProviderApiKeyCiphertext is not { } ciphertext)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        var apiKey = _secretProtector.Unprotect(ciphertext, ProviderCredentialPurpose.ForChannel(channel.Id));
        var baseUrl = _options.BaseUrlOf(channel.ProviderEnvironment).AbsoluteUri.TrimEnd('/');
        var templates = new List<TemplateSnapshot>();
        var skipped = 0;

        // The next page starts after what the provider actually returned, which may be fewer than asked for.
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = new Uri($"{baseUrl}/v1/configs/templates?limit={PageSize}&offset={offset.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(Dialog360MessageSender.ApiKeyHeader, apiKey);

            var (status, document) = await GetAsync(request, channel, cancellationToken);
            if (status != WhatsAppSendStatus.Sent)
            {
                return TemplateListResult.Failed(status);
            }

            using (document)
            {
                var root = document!.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("waba_templates", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("The template list of channel {ChannelId} came without waba_templates.", channel.Id);
                    return TemplateListResult.Failed(WhatsAppSendStatus.TransientFailure);
                }

                var count = 0;
                foreach (var item in list.EnumerateArray())
                {
                    count++;
                    if (Snapshot(item) is { } snapshot)
                    {
                        templates.Add(snapshot);
                    }
                    else
                    {
                        skipped++;
                    }
                }

                int? announced = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var value) ? value : null;
                var hasTotal = announced is not null;
                var total = announced ?? offset + count;

                // A page that comes empty before the provider's own total is a broken read, not a shorter list: storing it would make
                // the missing templates disappear.
                if (count == 0 && offset < total)
                {
                    _logger.LogWarning(
                        "The template list of channel {ChannelId} ended at {Read} of the {Total} templates 360dialog announced; it was not used.",
                        channel.Id, offset, total);
                    return TemplateListResult.Failed(WhatsAppSendStatus.TransientFailure);
                }

                if (count == 0 || offset + count >= total)
                {
                    _logger.LogInformation(
                        "Read {Count} templates of channel {ChannelId} from 360dialog ({Skipped} entries skipped).", templates.Count, channel.Id, skipped);
                    var complete = hasTotal && skipped == 0 && offset + count == total;
                    return new TemplateListResult(WhatsAppSendStatus.Sent, templates, complete);
                }

                offset += count;
            }
        }

        _logger.LogWarning("The template list of channel {ChannelId} has more than {Pages} pages; it was not read.", channel.Id, MaxPages);
        return TemplateListResult.Failed(WhatsAppSendStatus.Rejected);
    }

    private async Task<(WhatsAppSendStatus Status, JsonDocument? Document)> GetAsync(HttpRequestMessage request, WhatsAppChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode switch
            {
                >= 200 and < 300 => WhatsAppSendStatus.Sent,
                401 => WhatsAppSendStatus.InvalidCredentials,
                429 => WhatsAppSendStatus.LimitReached,
                408 or >= 500 => WhatsAppSendStatus.TransientFailure,
                >= 400 and < 500 => WhatsAppSendStatus.Rejected,
                _ => WhatsAppSendStatus.TransientFailure,
            };
            if (status != WhatsAppSendStatus.Sent)
            {
                _logger.LogWarning(
                    "360dialog refused the template list of channel {ChannelId} with HTTP {StatusCode}: {Outcome}.", channel.Id, (int)response.StatusCode, status);
                return (status, null);
            }

            var bytes = await Dialog360MessageSender.ReadAtMostAsync(response.Content, MaxPageBytes, cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                return (WhatsAppSendStatus.TransientFailure, null);
            }

            return (WhatsAppSendStatus.Sent, JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or DecoderFallbackException)
        {
            _logger.LogWarning("The template list of channel {ChannelId} could not be read: {ExceptionType}.", channel.Id, ex.GetType().Name);
            return (WhatsAppSendStatus.TransientFailure, null);
        }
    }

    // One entry of waba_templates; null when it is not a template Conversia can name (no valid name or language).
    private static TemplateSnapshot? Snapshot(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = String(item, "name");
        var language = String(item, "language");
        if (!MessageTemplate.IsValidName(name) || !MessageTemplate.IsValidLanguage(language))
        {
            return null;
        }

        var category = TemplateNames.CategoryOf(String(item, "category"));
        var (body, supported) = Components(item);
        return new TemplateSnapshot(name!, language!, category, TemplateNames.StatusOf(String(item, "status")), body,
            supported && category != TemplateCategory.Authentication);
    }

    // The body text, and whether Conversia can send the template with text values only: a text header without placeholders, a footer,
    // and quick-reply, phone or fixed-URL buttons are fine; media headers, header placeholders, buttons with variables or codes, carousels
    // and anything unknown are not (yet).
    private static (string? Body, bool Supported) Components(JsonElement item)
    {
        if (!item.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
        {
            return (null, false);
        }

        string? body = null;
        var supported = true;
        foreach (var component in components.EnumerateArray())
        {
            switch (String(component, "type")?.ToUpperInvariant())
            {
                case "BODY":
                    body = String(component, "text");
                    break;
                case "HEADER":
                    supported &= String(component, "format")?.ToUpperInvariant() == "TEXT" && !HasPlaceholder(String(component, "text"));
                    break;
                case "FOOTER":
                    break;
                case "BUTTONS":
                    supported &= component.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array
                                 && buttons.EnumerateArray().All(SupportedButton);
                    break;
                default:
                    supported = false;
                    break;
            }
        }

        return (body, supported && body is not null);
    }

    private static bool SupportedButton(JsonElement button) => String(button, "type")?.ToUpperInvariant() switch
    {
        "QUICK_REPLY" or "PHONE_NUMBER" => true,
        "URL" => !HasPlaceholder(String(button, "url")),
        _ => false,
    };

    private static bool HasPlaceholder(string? text) => text is not null && text.Contains("{{", StringComparison.Ordinal);

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;
}
