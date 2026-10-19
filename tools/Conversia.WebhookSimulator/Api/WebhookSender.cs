using System.Net;
using System.Text;
using Conversia.Application.DTOs.Organization;

namespace Conversia.WebhookSimulator.Api;

/// <summary>
/// POSTs deliveries to a channel's webhook the way 360dialog does: the JSON body and the secret in <c>X-Webhook-Secret</c>. It paces itself
/// (a constant number of requests per second, well under the limits of docs/WEBHOOKS.md), waits out a <c>429</c> for as long as
/// <c>Retry-After</c> says and retries a failing server a few times. A <c>401</c> stops everything: sending more would only spend the
/// failed-authentication budget of the address.
/// </summary>
public sealed class WebhookSender
{
    private readonly HttpClient _http;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private long _nextSlotTicks;

    public WebhookSender(HttpClient http, int requestsPerSecond, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, requestsPerSecond));
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>How many requests were sent, and how many times the API said to slow down.</summary>
    public int Sent { get; private set; }

    public int Throttled { get; private set; }

    public async Task SendAsync(string webhookPath, string secret, string json, CancellationToken cancellationToken)
    {
        await PaceAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookPath.TrimStart('/'))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation(WebhookRoutes.SecretHeader, secret);
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
