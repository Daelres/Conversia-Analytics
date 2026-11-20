using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Classification.Llm;

namespace Conversia.Infrastructure.UnitTests.Classification;

/// <summary>
/// The Message Batches adapter through the official SDK with a fake HTTP handler underneath (docs/CLASSIFICATION.md, "Batch mode"): the batch
/// the SDK puts on the wire (each request the same parameters as the synchronous call), the status it reads, the four result types of the
/// JSONL results, and failures as kinds. No test here reaches the network.
/// </summary>
public class AnthropicLlmBatchClientTests
{
    private const string FakeKey = "test-key-not-a-secret";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string type = "application/json")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static readonly AnthropicRequestSettings Settings = new("claude-haiku-5-5", MaxTokens: 2048, Effort: "low", DisableThinking: false);

    private static (AnthropicLlmBatchClient Client, FakeHandler Handler) New(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var sdk = AnthropicLlmClient.CreateSdkClient(FakeKey, AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(30), maxRetries: 0, new HttpClient(handler));
        return (new AnthropicLlmBatchClient(sdk, Settings), handler);
    }

    private static LlmRequest Request(string text) => new(
        "SYSTEM PROMPT", "CONTEXT BLOCK", $"<conversation>\n1 C: {text}\n</conversation>",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["stage"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("stage"), ["additionalProperties"] = false },
        new string('a', 64));

    private static string BatchJson(string status, int processing = 0, int succeeded = 0, int errored = 0, int expired = 0, int canceled = 0) => new JsonObject
    {
        ["id"] = "msgbatch_test",
        ["type"] = "message_batch",
        ["processing_status"] = status,
        ["request_counts"] = new JsonObject { ["processing"] = processing, ["succeeded"] = succeeded, ["errored"] = errored, ["canceled"] = canceled, ["expired"] = expired },
        ["ended_at"] = status == "ended" ? "2026-11-12T06:40:00Z" : null,
        ["created_at"] = "2026-11-12T06:00:00Z",
        ["expires_at"] = "2026-11-13T06:00:00Z",
        ["archived_at"] = null,
        ["cancel_initiated_at"] = null,
        ["results_url"] = status == "ended" ? "https://api.anthropic.com/v1/messages/batches/msgbatch_test/results" : null,
    }.ToJsonString();

    [Fact]
    public async Task ABatch_OnTheWire_CarriesEachRequestWithItsCustomId_AndTheSynchronousParameters()
    {
        var (client, handler) = New(_ => Answer(HttpStatusCode.OK, BatchJson("in_progress", processing: 2)));

        var submission = await client.SubmitAsync([new LlmBatchItem("c1-aaa", Request("hola")), new LlmBatchItem("c2-bbb", Request("precio"))]);

        Assert.Equal(("msgbatch_test", new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc)), (submission.ProviderBatchId, submission.ExpiresAt));
        var (sent, body) = Assert.Single(handler.Seen);
        Assert.Equal((HttpMethod.Post, "/v1/messages/batches"), (sent.Method, sent.RequestUri!.AbsolutePath));
        var requests = JsonNode.Parse(body)!["requests"]!.AsArray();
        Assert.Equal(["c1-aaa", "c2-bbb"], requests.Select(r => r!["custom_id"]!.GetValue<string>()));
        var first = requests[0]!["params"]!;
        Assert.Equal(("claude-haiku-5-5", 2048), (first["model"]!.GetValue<string>(), first["max_tokens"]!.GetValue<int>()));
        Assert.Equal("low", first["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", first["output_config"]!["format"]!["type"]!.GetValue<string>());
        var system = first["system"]!.AsArray();
        Assert.Equal(("SYSTEM PROMPT", "CONTEXT BLOCK", "ephemeral"),
            (system[0]!["text"]!.GetValue<string>(), system[1]!["text"]!.GetValue<string>(), system[1]!["cache_control"]!["type"]!.GetValue<string>()));
        Assert.Null(system[0]!["cache_control"]);
        Assert.Contains("1 C: hola", first["messages"]![0]!["content"]!.ToJsonString());
        Assert.Null(first["stream"]);
        Assert.DoesNotContain("snapshot", body, StringComparison.OrdinalIgnoreCase); // the hash is ours, never sent
    }

    [Fact]
    public async Task TheStatus_IsReadWithItsCounts()
    {
        var (client, _) = New(_ => Answer(HttpStatusCode.OK, BatchJson("ended", succeeded: 3, errored: 1, expired: 2, canceled: 0)));

        var state = await client.GetAsync("msgbatch_test");

        Assert.Equal(new LlmBatchState(LlmBatchProcessing.Ended, 0, 3, 1, 0, 2, new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc)), state);
    }

    [Fact]
    public async Task TheResults_AreReadInAnyOrder_AsTheFourResultTypes_ASucceededOneLikeASynchronousAnswer()
    {
        var lines = string.Join('\n',
            new JsonObject { ["custom_id"] = "c2", ["result"] = new JsonObject { ["type"] = "succeeded", ["message"] = JsonNode.Parse(AnthropicLlmClientTests.MessageJson()) } }.ToJsonString(),
            new JsonObject { ["custom_id"] = "c1", ["result"] = new JsonObject { ["type"] = "errored", ["error"] = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = "texto del proveedor" } } } }.ToJsonString(),
            new JsonObject { ["custom_id"] = "c3", ["result"] = new JsonObject { ["type"] = "expired" } }.ToJsonString(),
            new JsonObject { ["custom_id"] = "c4", ["result"] = new JsonObject { ["type"] = "canceled" } }.ToJsonString());
        var (client, handler) = New(request => request.RequestUri!.AbsolutePath.EndsWith("/results", StringComparison.Ordinal)
            ? Answer(HttpStatusCode.OK, lines, "application/binary")
            : Answer(HttpStatusCode.OK, BatchJson("ended", succeeded: 1, errored: 1, expired: 1, canceled: 1)));

        var results = new List<LlmBatchResult>();
        await foreach (var result in client.ResultsAsync("msgbatch_test"))
        {
            results.Add(result);
        }

        Assert.Equal(["c2", "c1", "c3", "c4"], results.Select(r => r.CustomId));
        Assert.Equal([LlmBatchResultKind.Succeeded, LlmBatchResultKind.Errored, LlmBatchResultKind.Expired, LlmBatchResultKind.Canceled], results.Select(r => r.Kind));
        var completion = results[0].Completion!;
        Assert.Equal((LlmStopReason.EndTurn, 900L, 3000L), (completion.StopReason, completion.Usage.InputTokens, completion.Usage.CacheReadTokens));
        Assert.Equal(("invalid_request_error", (LlmCompletion?)null), (results[1].ErrorType, results[1].Completion));
        Assert.DoesNotContain(results, r => r.ErrorType?.Contains("texto", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailureKind.Misconfigured)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmFailureKind.Unavailable)]
    public async Task AFailedCall_IsAKind_NeverTheProvidersText(HttpStatusCode status, LlmFailureKind kind)
    {
        var (client, _) = New(_ => Answer(status, """{"type":"error","error":{"type":"x","message":"texto del proveedor"}}"""));

        var ex = await Assert.ThrowsAsync<LlmCallException>(() => client.SubmitAsync([new LlmBatchItem("c1", Request("hola"))]));

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("texto", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingABatch_IsNeverResentByTheSdk_EvenOnARetryableStatus()
    {
        // Batches.Create is not idempotent: a retried upload could create (and bill) a second batch of the same conversations.
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.ServiceUnavailable, """{"type":"error","error":{"type":"overloaded_error","message":"x"}}"""));
        var client = AnthropicLlmBatchClient.Create(FakeKey, AnthropicLlmClient.OfficialBaseUrl, Settings, new HttpClient(handler));

        await Assert.ThrowsAsync<LlmCallException>(() => client.SubmitAsync([new LlmBatchItem("c1", Request("hola"))]));

        Assert.Single(handler.Seen);
        Assert.Equal(0, AnthropicLlmBatchClient.SdkMaxRetries);
    }

    [Fact]
    public async Task TheList_ReadsEveryPage_NewestFirst_WithTheTotalOfRequests()
    {
        var first = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(BatchJson("in_progress", processing: 2)), JsonNode.Parse(BatchJson("ended", succeeded: 1, errored: 1))),
            ["has_more"] = true, ["first_id"] = "msgbatch_test", ["last_id"] = "msgbatch_test",
        }.ToJsonString();
        var second = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(BatchJson("ended", expired: 3))), ["has_more"] = false, ["first_id"] = "msgbatch_test", ["last_id"] = "msgbatch_test" }.ToJsonString();
        var (client, handler) = New(request => Answer(HttpStatusCode.OK, request.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal) ? second : first));

        var listed = new List<LlmBatchSummary>();
        await foreach (var batch in client.ListAsync())
        {
            listed.Add(batch);
        }

        Assert.Equal([2, 2, 3], listed.Select(b => b.Requests));
        Assert.Equal([LlmBatchProcessing.InProgress, LlmBatchProcessing.Ended, LlmBatchProcessing.Ended], listed.Select(b => b.Processing));
        Assert.Equal(2, handler.Seen.Count);
    }
}
