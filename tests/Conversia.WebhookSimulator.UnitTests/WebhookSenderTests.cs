using System.Net;
using Conversia.WebhookSimulator.Api;

namespace Conversia.WebhookSimulator.UnitTests;

public class WebhookSenderTests
{
    private const string Secret = "dummy-secret-for-tests-0123456789";

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;

        public List<(HttpMethod Method, string Path, string? Secret, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-Webhook-Secret", out var values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var step = script[Math.Min(_next++, script.Length - 1)];
            return step(request);
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode status, int? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static (WebhookSender Sender, ScriptedHandler Handler, List<TimeSpan> Delays) Create(int rate, params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var delays = new List<TimeSpan>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5030/") };
        var sender = new WebhookSender(http, rate, (span, _) => { delays.Add(span); return Task.CompletedTask; });
        return (sender, handler, delays);
    }

    [Fact]
    public async Task A_delivery_is_a_json_post_with_the_secret_in_the_header()
    {
        var (sender, handler, _) = Create(1000, _ => Status(HttpStatusCode.OK));

        await sender.SendAsync("/api/webhooks/360dialog/some-key", Secret, """{"object":"x"}""", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/webhooks/360dialog/some-key", request.Path);
        Assert.Equal(Secret, request.Secret);
        Assert.Equal("""{"object":"x"}""", request.Body);
        Assert.Equal(1, sender.Sent);
    }

    [Fact]
    public async Task A_429_is_waited_out_for_as_long_as_the_api_asks_and_then_retried()
    {
        var (sender, handler, delays) = Create(1000, _ => Status(HttpStatusCode.TooManyRequests, 7), _ => Status(HttpStatusCode.OK));

        await sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(7), delays);
        Assert.Equal(1, sender.Throttled);
    }

    [Fact]
    public async Task A_wait_longer_than_a_minute_is_capped_and_a_missing_header_is_a_few_seconds()
    {
        var (sender, _, delays) = Create(1000, _ => Status(HttpStatusCode.TooManyRequests, 3600), _ => Status(HttpStatusCode.TooManyRequests), _ => Status(HttpStatusCode.OK));

        await sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None);

        Assert.Contains(TimeSpan.FromSeconds(60), delays);
        Assert.Contains(TimeSpan.FromSeconds(5), delays);
    }

    [Fact]
    public async Task A_401_stops_at_once_without_retrying_and_without_the_secret_in_the_message()
    {
        var (sender, handler, _) = Create(1000, _ => Status(HttpStatusCode.Unauthorized));

        var failure = await Assert.ThrowsAsync<ApiFailure>(() => sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.Status);
        Assert.DoesNotContain(Secret, failure.Message);
        Assert.DoesNotContain("/k", failure.Message);
        Assert.Contains("SIM_WEBHOOK_SECRET", failure.Message);
    }

    [Fact]
    public async Task A_failing_server_is_retried_with_a_growing_pause_and_then_gives_up()
    {
        var (sender, handler, delays) = Create(1000, _ => Status(HttpStatusCode.BadGateway));

        var failure = await Assert.ThrowsAsync<ApiFailure>(() => sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None));

        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)], delays.Where(d => d >= TimeSpan.FromSeconds(1)));
        Assert.Equal(HttpStatusCode.BadGateway, failure.Status);
    }

    [Fact]
    public async Task A_refused_request_is_not_retried_forever_and_says_the_webhook_could_not_be_reached()
    {
        var (sender, _, _) = Create(1000, _ => throw new HttpRequestException("connection refused"));

        var failure = await Assert.ThrowsAsync<ApiFailure>(() => sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None));

        Assert.Contains("could not be reached", failure.Message);
    }

    [Fact]
    public async Task A_400_is_a_failure_with_the_status_and_no_body()
    {
        var (sender, _, _) = Create(1000, _ => Status(HttpStatusCode.BadRequest));

        var failure = await Assert.ThrowsAsync<ApiFailure>(() => sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None));

        Assert.Contains("400", failure.Message);
    }

    [Fact]
    public async Task The_rate_spaces_the_requests_so_a_burst_cannot_exceed_it()
    {
        var (sender, _, delays) = Create(10, _ => Status(HttpStatusCode.OK));

        for (var i = 0; i < 5; i++)
        {
            await sender.SendAsync("/api/webhooks/360dialog/k", Secret, "{}", CancellationToken.None);
        }

        // Ten per second: after the first one every request waits (the stand-in delay does not really sleep, so the slots pile up).
        Assert.True(delays.Count >= 3, $"Only {delays.Count} pauses for 5 requests at 10 per second.");
        Assert.True(delays.Zip(delays.Skip(1), (a, b) => b >= a).All(x => x), "The waits grow as the slots fill.");
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Retry_after_is_between_one_second_and_one_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ConversiaApi.RetryAfter(Status(HttpStatusCode.TooManyRequests, 0)));
        Assert.Equal(TimeSpan.FromSeconds(30), ConversiaApi.RetryAfter(Status(HttpStatusCode.TooManyRequests, 30)));
        Assert.Equal(TimeSpan.FromSeconds(60), ConversiaApi.RetryAfter(Status(HttpStatusCode.TooManyRequests, 99999)));
    }
}
