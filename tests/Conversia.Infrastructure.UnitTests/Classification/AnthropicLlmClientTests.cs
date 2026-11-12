using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Classification.Llm;

namespace Conversia.Infrastructure.UnitTests.Classification;

/// <summary>
/// The Anthropic adapter (design D3, spikes S1-S7) driven through the official SDK with a fake HTTP handler underneath: the request the SDK
/// puts on the wire, the answer it parses, and each failure kind. No test here reaches the network.
/// </summary>
public class AnthropicLlmClientTests
{
    private const string FakeKey = "test-key-not-a-secret";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static string MessageJson(string? text = "{\"stage\":\"undetermined\"}", string stopReason = "end_turn", int cacheRead = 3000, int cacheWrite = 12)
    {
        var content = new JsonArray();
        if (text is not null)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        return new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-haiku-5-5",
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 900,
                ["output_tokens"] = 60,
                ["cache_creation_input_tokens"] = cacheWrite,
                ["cache_read_input_tokens"] = cacheRead,
            },
        }.ToJsonString();
    }

    private static readonly AnthropicRequestSettings Settings = new("claude-haiku-5-5", MaxTokens: 2048, Effort: "low", DisableThinking: false);

    private static (AnthropicLlmClient Client, FakeHandler Handler) New(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, AnthropicRequestSettings? settings = null, TimeSpan? timeout = null,
        Uri? baseUrl = null)
    {
        var handler = new FakeHandler(respond);
        var sdk = AnthropicLlmClient.CreateSdkClient(FakeKey, baseUrl ?? AnthropicLlmClient.OfficialBaseUrl, timeout ?? TimeSpan.FromSeconds(8), maxRetries: 0, new HttpClient(handler));
        return (new AnthropicLlmClient(sdk, settings ?? Settings), handler);
    }

    private static readonly LlmRequest Request = new(
        "SYSTEM PROMPT", "CONTEXT BLOCK", "<conversation>\n[#1][C] hola\n</conversation>",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["stage"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("stage"), ["additionalProperties"] = false },
        new string('a', 64));

    [Fact]
    public async Task TheRequest_OnTheWire_IsOneMessagesCall_WithTheCacheablePrefix_TheSchema_AndTheEffort()
    {
        var (client, handler) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson())));

        await client.CompleteAsync(Request);

        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("claude-haiku-5-5", json["model"]!.GetValue<string>());
        Assert.Equal(2048, json["max_tokens"]!.GetValue<int>());

        // S2: two system blocks, the breakpoint on the last one, so the whole static prefix (prompt + catalog) is cached.
        var system = json["system"]!.AsArray();
        Assert.Equal(["SYSTEM PROMPT", "CONTEXT BLOCK"], system.Select(b => b!["text"]!.GetValue<string>()));
        Assert.Null(system[0]!["cache_control"]);
        Assert.Equal("ephemeral", system[1]!["cache_control"]!["type"]!.GetValue<string>());

        var messages = json["messages"]!.AsArray();
        var user = Assert.Single(messages)!;
        Assert.Equal("user", user["role"]!.GetValue<string>());
        Assert.Contains("[#1][C] hola", user.ToJsonString());

        // S1 / S5: structured output with the schema, effort inside output_config, thinking left to the model's default (adaptive).
        Assert.Equal("json_schema", json["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal(Request.Schema.ToJsonString(), json["output_config"]!["format"]!["schema"]!.ToJsonString());
        Assert.Equal("low", json["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(json["thinking"]);

        // Nothing the model rejects (sampling parameters, prefill, forced tools) and never the snapshot hash.
        foreach (var absent in new[] { "temperature", "top_p", "top_k", "tools", "tool_choice" })
        {
            Assert.Null(json[absent]);
        }

        Assert.DoesNotContain(Request.SnapshotHash, body);
    }

    [Fact]
    public async Task DisabledThinking_IsSentExplicitly()
    {
        var (client, handler) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson())), Settings with { DisableThinking = true });

        await client.CompleteAsync(Request);

        Assert.Equal("disabled", JsonNode.Parse(handler.Seen[0].Body)!["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheAnswer_IsTheTextOfTheMessage_WithEveryUsageField_AndTheModel()
    {
        var (client, _) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson("{\"stage\":\"won\"}"))));

        var completion = await client.CompleteAsync(Request);

        Assert.Equal(LlmStopReason.EndTurn, completion.StopReason);
        Assert.Equal("{\"stage\":\"won\"}", completion.JsonText);
        Assert.Equal(new LlmUsage(InputTokens: 900, OutputTokens: 60, CacheReadTokens: 3000, CacheWriteTokens: 12), completion.Usage);
        Assert.Equal("claude-haiku-5-5", completion.ModelId);
    }

    [Theory]
    [InlineData("refusal", LlmStopReason.Refusal)]
    [InlineData("max_tokens", LlmStopReason.MaxTokens)]
    [InlineData("pause_turn", LlmStopReason.Other)]
    [InlineData("tool_use", LlmStopReason.Other)]
    public async Task EveryOtherStopReason_IsReported(string wire, LlmStopReason expected)
    {
        var (client, _) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson(stopReason: wire))));

        Assert.Equal(expected, (await client.CompleteAsync(Request)).StopReason);
    }

    [Fact]
    public async Task AnAnswerWithoutText_HasNoJson()
    {
        var (client, _) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson(text: null, stopReason: "refusal"))));

        Assert.Null((await client.CompleteAsync(Request)).JsonText);
    }

    [Theory]
    [InlineData(429, LlmFailureKind.RateLimited)]
    [InlineData(500, LlmFailureKind.Unavailable)]
    [InlineData(503, LlmFailureKind.Unavailable)]
    [InlineData(529, LlmFailureKind.Unavailable)]
    [InlineData(400, LlmFailureKind.Unavailable)]
    public async Task AnErrorStatus_IsATransportFailure_NamingTheExceptionTypeOnly(int status, LlmFailureKind expected)
    {
        // The body echoes request text, as a provider error might: it must not reach the exception message.
        var (client, handler) = New((_, _) => Task.FromResult(Json((HttpStatusCode)status,
            "{\"type\":\"error\",\"error\":{\"type\":\"some_error\",\"message\":\"echo: [#1][C] hola\"}}")));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("hola", ex.Message);
        Assert.StartsWith("Anthropic", ex.ErrorType);
        Assert.Single(handler.Seen); // no retry inside the call (Stage A: Llm:MaxRetries = 0)
    }

    [Theory]
    [InlineData(401, "authentication")]
    [InlineData(403, "permission")]
    [InlineData(404, "model_not_found")]
    public async Task ARejectedConfiguration_IsMisconfigured_NamingTheCondition(int status, string condition)
    {
        var (client, _) = New((_, _) => Task.FromResult(Json((HttpStatusCode)status, "{\"type\":\"error\",\"error\":{\"type\":\"x\",\"message\":\"echo [#1][C] hola\"}}")));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal((LlmFailureKind.Misconfigured, condition), (ex.Kind, ex.ErrorType));
        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain("hola", ex.Message);
    }

    [Fact]
    public async Task TheCall_GoesToTheConfiguredBaseUrl_WithTelemetryOff()
    {
        var (client, handler) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, MessageJson())), baseUrl: new Uri("https://llm-gateway.example.test/"));

        await client.CompleteAsync(Request);

        Assert.Equal("llm-gateway.example.test", handler.Seen[0].Request.RequestUri!.Host);
        Assert.Equal("https", handler.Seen[0].Request.RequestUri!.Scheme);
    }

    [Fact]
    public async Task OneAttemptAndTheTimeout_BoundTheWallClockTime_ThroughTheRealSdkClient()
    {
        // Stage A holds the conversation row during the call: MaxRetries=0 plus the timeout must bound it, whatever the provider does.
        var (client, handler) = New(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(HttpStatusCode.OK, MessageJson());
        }, timeout: TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal(LlmFailureKind.Timeout, ex.Kind);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(3));
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task ARetryableStatus_IsNotRetried_WithMaxRetriesZero()
    {
        var (client, handler) = New((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"x\"}}")));

        await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task ANetworkFailure_IsUnavailable()
    {
        var (client, _) = New((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal(LlmFailureKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task ACallLongerThanTheTimeout_IsATimeout()
    {
        var (client, _) = New(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Json(HttpStatusCode.OK, MessageJson());
        }, timeout: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal(LlmFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotAFailureOfTheProvider()
    {
        // The host is stopping (or the runner's budget ran out): the cancellation propagates, nothing is blamed on the provider.
        using var cancel = new CancellationTokenSource();
        var (client, _) = New(async (_, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Json(HttpStatusCode.OK, MessageJson());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Request, cancel.Token));
    }

    [Fact]
    public async Task AnUnreadableAnswer_IsInvalidOutput()
    {
        var (client, _) = New((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":")));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.CompleteAsync(Request));

        Assert.Equal(LlmFailureKind.InvalidOutput, ex.Kind);
    }
}
