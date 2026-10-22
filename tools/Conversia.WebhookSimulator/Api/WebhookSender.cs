using System.Net;
using System.Text;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.Ingestion;

namespace Conversia.WebhookSimulator.Api;

/// <summary>
/// POSTs deliveries to a channel's webhook the way 360dialog does: the JSON body and the secret in <c>X-Webhook-Secret</c> (or, with <c>--auth basic</c>, as the password of <c>Authorization: Basic</c>). It paces itself
/// (a constant number of requests per second, well under the limits of docs/WEBHOOKS.md), waits out a <c>429</c> for as long as
/// <c>Retry-After</c> says and retries a failing server a few times. A <c>401</c> stops everything: sending more would only spend the
/// failed-authentication budget of the address.
/// </summary>
public sealed class WebhookSender
{
    private readonly HttpClient _http;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _basicAuth;
    private long _nextSlotTicks;

    /// <param name="http">A client of its own: same base address as the API, no redirects and NO default <c>Authorization</c> header. The owner's
    /// token belongs to <see cref="ConversiaApi"/> only; a webhook delivery carries just the channel secret.</param>
    /// <param name="basicAuth">Present the secret as <c>Authorization: Basic base64(conversia:secret)</c> instead of <c>X-Webhook-Secret</c>.</param>
    public WebhookSender(HttpClient http, int requestsPerSecond, Func<TimeSpan, CancellationToken, Task>? delay = null, bool basicAuth = false)
    {
        if (http.DefaultRequestHeaders.Authorization is not null)
        {
            throw new ArgumentException("The webhook sender must not share the client that carries the API token.", nameof(http));
        }

        _http = http;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, requestsPerSecond));
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _basicAuth = basicAuth;
    }

    /// <summary>How many requests were sent, and how many times the API said to slow down.</summary>
    public int Sent { get; private set; }

    public int Throttled { get; private set; }

    /// <summary>
    /// The path comes from an API answer, so it is not trusted: only a relative path under <c>/api/webhooks/360dialog/</c> is accepted. Anything
    /// else (an absolute address, a scheme-relative <c>//host</c>, a backslash, <c>..</c>, a query, an escape, a control character) would let
    /// the secret travel to a host or a route other than the channel's webhook. The error never quotes the path: it holds the channel key.
    /// </summary>
    public static string ValidatePath(string? webhookPath)
    {
        var prefix = "/" + WebhookRoutes.Dialog360Base + "/";
        if (webhookPath is null
            || webhookPath.Length <= prefix.Length
            || !webhookPath.StartsWith(prefix, StringComparison.Ordinal)
            || webhookPath.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '?' or '#' or '%')
            || webhookPath.Contains("..", StringComparison.Ordinal)
            || webhookPath.Contains("//", StringComparison.Ordinal))
        {
            throw new ApiFailure(
                $"The API gave a webhook path that is not a relative '{prefix}...' path. The simulator refuses to send a secret to it.");
        }

        return webhookPath.TrimStart('/');
    }

    public async Task SendAsync(string webhookPath, string secret, string json, CancellationToken cancellationToken)
    {
        var relativePath = ValidatePath(webhookPath);
        await PaceAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (_basicAuth)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", WebhookBasicAuth.Header(secret));
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(WebhookRoutes.SecretHeader, secret);
                }

                using var response = await _http.SendAsync(request, cancellationToken);
                Sent++;

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        return;
                    case HttpStatusCode.Unauthorized:
                        throw new ApiFailure(
                            "The webhook answered 401: the channel is unknown or inactive, or its secret is wrong. " +
                            "Check SIM_WEBHOOK_SECRETS / SIM_WEBHOOK_SECRET (or run setup --rotate-secret --print-secret).", response.StatusCode);
                    case HttpStatusCode.TooManyRequests when attempt <= 8:
                        Throttled++;
                        await _delay(ConversiaApi.RetryAfter(response), cancellationToken);
                        continue;
                    case HttpStatusCode.TooManyRequests:
                        throw new ApiFailure("The webhook kept answering 429; slow down with --rate and try again.", response.StatusCode);
                    case >= HttpStatusCode.InternalServerError when attempt <= 5:
                        await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
                        continue;
                    default:
                        throw new ApiFailure($"The webhook answered {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode);
                }
            }
            catch (HttpRequestException) when (attempt <= 5)
            {
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new ApiFailure("The webhook could not be reached.");
            }
        }
    }

    // One slot per interval, so the rate holds however long a request takes.
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.Ticks;
        var slot = Math.Max(now, _nextSlotTicks);
        _nextSlotTicks = slot + _interval.Ticks;
        if (slot > now)
        {
            await _delay(TimeSpan.FromTicks(slot - now), cancellationToken);
        }
    }
}
