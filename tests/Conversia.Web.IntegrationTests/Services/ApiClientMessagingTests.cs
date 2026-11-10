using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Services;

/// <summary>
/// The messaging calls of the API client: their request shape, and that the machine <c>code</c> of a problem answer reaches the caller
/// for every status the messaging endpoints use (409, 422, 429, 503 and the 500 that carries no error id).
/// </summary>
public partial class ApiClientTests
{
    private static readonly Guid ChannelId = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid ConversationId = Guid.Parse("d0000000-0000-4000-8000-000000000001");

    private const string DummyKey = "d360-dummy-key-Zx9";

    private static string Problem(int status, string? code, string detail = "Texto del problema.")
        => code is null
            ? $$"""{"title":"x","status":{{status}},"detail":"{{detail}}"}"""
            : $$"""{"title":"x","status":{{status}},"detail":"{{detail}}","code":"{{code}}"}""";

    [Fact]
    public async Task Setting_the_credentials_puts_the_key_and_the_environment_and_returns_the_channel()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new { id = ChannelId, hasProviderApiKey = true, providerEnvironment = "Sandbox" }));

        var result = await Client(stub).SetProviderCredentialsAsync("t", ChannelId, new SetProviderCredentialsRequest { ApiKey = DummyKey, Environment = "Sandbox" });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HasProviderApiKey);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal($"/api/channels/{ChannelId}/provider-credentials", call.Uri.AbsolutePath);
        Assert.Equal(string.Empty, call.Uri.Query); // the key never travels in an address
        Assert.Equal("Bearer t", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(DummyKey, body.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal("Sandbox", body.RootElement.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task Removing_the_credentials_is_a_delete_without_a_body()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new { id = ChannelId, hasProviderApiKey = false, providerEnvironment = "Sandbox" }));

        var result = await Client(stub).RemoveProviderCredentialsAsync("t", ChannelId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.HasProviderApiKey);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal($"/api/channels/{ChannelId}/provider-credentials", call.Uri.AbsolutePath);
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task Sending_a_message_posts_the_text_and_returns_the_stored_message()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.Created, new { id = Guid.NewGuid(), direction = "Outbound", type = "text", text = "Hola", sentAt = DateTime.UtcNow }));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.True(result.IsSuccess);
        Assert.Equal("Outbound", result.Value!.Direction);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"/api/conversations/{ConversationId}/messages", call.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("Hola", body.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(409, "conversation_closed", ApiResultStatus.Conflict)]
    [InlineData(409, "channel_inactive", ApiResultStatus.Conflict)]
    [InlineData(409, "provider_credentials_missing", ApiResultStatus.Conflict)]
    [InlineData(409, "provider_credentials_invalid", ApiResultStatus.Conflict)]
    [InlineData(422, "recipient_not_allowed", ApiResultStatus.Unprocessable)]
    [InlineData(422, "provider_rejected", ApiResultStatus.Unprocessable)]
    [InlineData(429, "provider_limit_reached", ApiResultStatus.RateLimited)]
    [InlineData(503, "provider_unavailable", ApiResultStatus.Unavailable)]
    [InlineData(503, "provider_credentials_unavailable", ApiResultStatus.Unavailable)]
    [InlineData(500, "message_sent_not_recorded", ApiResultStatus.Unavailable)]
    public async Task A_problem_with_a_code_carries_the_code_and_is_not_an_unexpected_error(int status, string code, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, Problem(status, code)));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.ProblemCode);
        Assert.Null(result.ErrorCode); // an answer the API worded itself: nothing to report, no support code to show
    }

    [Fact]
    public async Task The_apis_own_rate_limit_has_a_retry_after_and_no_code()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, Problem(429, null), retryAfter: "12"));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.RateLimited, result.Status);
        Assert.Null(result.ProblemCode);
        Assert.Equal(12, result.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(422)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_problem_without_a_code_is_an_unexpected_failure_with_an_error_code(int status)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, Problem(status, null)));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Null(result.ProblemCode);
        Assert.True(Conversia.Domain.Common.ErrorCode.IsValid(result.ErrorCode));
    }

    // A code nobody declared is not an answer anybody worded: it must not turn a real failure (a database that is down, a new
    // kind of problem) into a quiet "expected" outcome with no error code.
    [Theory]
    [InlineData(422, "something_new")]
    [InlineData(500, "database_unavailable")]
    [InlineData(503, "database_unavailable")]
    public async Task A_code_the_call_does_not_expect_is_an_unexpected_failure_with_an_error_code(int status, string code)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, Problem(status, code)));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Null(result.ProblemCode);
        Assert.True(Conversia.Domain.Common.ErrorCode.IsValid(result.ErrorCode));
    }

    [Theory]
    [InlineData(409, ApiResultStatus.Conflict)]
    [InlineData(429, ApiResultStatus.RateLimited)]
    public async Task A_code_the_call_does_not_expect_is_dropped_from_the_statuses_that_need_none(int status, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, Problem(status, "something_new")));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(expected, result.Status);
        Assert.Null(result.ProblemCode);
    }

    [Theory]
    [InlineData(503, "provider_unavailable")]
    [InlineData(500, "message_sent_not_recorded")]
    [InlineData(422, "provider_rejected")]
    public async Task A_messaging_code_on_a_call_that_is_not_a_send_is_an_unexpected_failure(int status, string code)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, Problem(status, code)));
        var client = Client(stub);

        var read = await client.GetUsersAsync("t");
        var credentials = await client.SetProviderCredentialsAsync("t", ChannelId, new SetProviderCredentialsRequest { ApiKey = DummyKey, Environment = "Sandbox" });

        Assert.All(new IApiOutcome[] { read, credentials }, outcome =>
        {
            Assert.Equal(ApiResultStatus.Unavailable, outcome.Status);
            Assert.True(Conversia.Domain.Common.ErrorCode.IsValid(outcome.ErrorCode));
        });
        Assert.Null(read.ProblemCode);
        Assert.Null(credentials.ProblemCode);
    }

    [Fact]
    public async Task The_credentials_calls_expect_only_the_missing_master_key()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, Problem(503, "provider_credentials_unavailable")));
        var client = Client(stub);

        var set = await client.SetProviderCredentialsAsync("t", ChannelId, new SetProviderCredentialsRequest { ApiKey = DummyKey, Environment = "Sandbox" });
        var removed = await client.RemoveProviderCredentialsAsync("t", ChannelId);

        Assert.Equal("provider_credentials_unavailable", set.ProblemCode);
        Assert.Equal("provider_credentials_unavailable", removed.ProblemCode);
        Assert.Null(set.ErrorCode);
    }

    [Fact]
    public async Task A_500_with_an_error_id_and_an_expected_code_keeps_both()
    {
        var errorId = Conversia.Domain.Common.ErrorCode.New(DateTime.UtcNow);
        var body = JsonSerializer.Serialize(new { title = "x", status = 500, detail = "d", code = "message_sent_not_recorded", errorId });
        var stub = new StubHandler(_ => Respond(HttpStatusCode.InternalServerError, body));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Equal(errorId, result.ErrorCode); // the failure the API registered is the one the person can quote
        Assert.Equal("message_sent_not_recorded", result.ProblemCode); // and the page still says the message left
    }

    [Fact]
    public async Task A_500_with_an_error_id_and_a_code_nobody_expects_is_the_registered_failure_alone()
    {
        var errorId = Conversia.Domain.Common.ErrorCode.New(DateTime.UtcNow);
        var body = JsonSerializer.Serialize(new { title = "x", status = 500, detail = "d", code = "something_new", errorId });
        var stub = new StubHandler(_ => Respond(HttpStatusCode.InternalServerError, body));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Equal(errorId, result.ErrorCode);
        Assert.Null(result.ProblemCode);
    }

    // ---- how long a send may take ----

    private static ApiClient TimedClient(HttpMessageHandler handler, int timeoutSeconds, int sendTimeoutSeconds)
        => new(new HttpClient(handler) { BaseAddress = ApiBase, Timeout = Timeout.InfiniteTimeSpan },
            options: Microsoft.Extensions.Options.Options.Create(new ApiOptions { BaseUrl = ApiBase.ToString(), TimeoutSeconds = timeoutSeconds, SendTimeoutSeconds = sendTimeoutSeconds }));

    /// <summary>An API that takes its time and, like a real connection, stops when the caller gives up.</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return request.Method == HttpMethod.Get
                ? await Json(HttpStatusCode.OK, Array.Empty<object>())
                : await Json(HttpStatusCode.Created, new { id = Guid.NewGuid(), direction = "Outbound", type = "text", text = "Hola", sentAt = DateTime.UtcNow });
        }
    }

    private static SlowHandler SlowStub(TimeSpan delay) => new(delay);

    [Fact]
    public async Task A_send_waits_its_own_longer_limit_while_every_other_call_keeps_the_usual_one()
    {
        var client = TimedClient(SlowStub(TimeSpan.FromSeconds(2.5)), timeoutSeconds: 1, sendTimeoutSeconds: 30);

        var read = await client.GetUsersAsync("t");
        var sent = await client.SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, read.Status); // gave up after a second
        Assert.True(sent.IsSuccess); // waited for the API, which was still working
    }

    [Fact]
    public async Task A_send_that_outlasts_its_limit_is_an_unexpected_failure_with_an_error_code()
    {
        var client = TimedClient(SlowStub(TimeSpan.FromSeconds(30)), timeoutSeconds: 1, sendTimeoutSeconds: 1);

        var sent = await client.SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Unavailable, sent.Status);
        Assert.Null(sent.ProblemCode);
        Assert.True(Conversia.Domain.Common.ErrorCode.IsValid(sent.ErrorCode));
    }

    [Fact]
    public void The_default_limit_of_a_send_outlasts_the_apis_call_to_the_provider_and_its_record_step()
    {
        using var api = JsonDocument.Parse(File.ReadAllText(Path.Combine(Infrastructure.RepoPaths.Root(), "src", "Conversia.API", "appsettings.json")));
        var provider = api.RootElement.GetProperty("Dialog360").GetProperty("TimeoutSeconds").GetInt32();
        var record = api.RootElement.GetProperty("Messaging").GetProperty("RecordTimeoutSeconds").GetInt32();
        using var web = JsonDocument.Parse(File.ReadAllText(Path.Combine(Infrastructure.RepoPaths.Root(), "src", "Conversia.Web", "appsettings.json")));
        var configured = web.RootElement.GetProperty("Api").GetProperty("SendTimeoutSeconds").GetInt32();

        Assert.True(new ApiOptions().SendTimeoutSeconds > provider + record, "the Web would give up on a send the API is still finishing");
        Assert.True(configured > provider + record);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void The_limit_of_a_send_is_validated_at_startup(int seconds, bool valid)
        => Assert.Equal(valid, ApiOptions.IsValid(new ApiOptions { BaseUrl = "http://api.test/", TimeoutSeconds = 10, SendTimeoutSeconds = seconds }));

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Provider_Unavailable")]
    [InlineData("code with spaces")]
    [InlineData("")]
    public async Task A_code_that_is_not_a_machine_name_is_never_carried(string code)
    {
        var body = JsonSerializer.Serialize(new { title = "x", status = 409, detail = "d", code });
        var stub = new StubHandler(_ => Respond(HttpStatusCode.Conflict, body));

        var result = await Client(stub).SendMessageAsync("t", ConversationId, new SendMessageRequest { Text = "Hola" });

        Assert.Equal(ApiResultStatus.Conflict, result.Status);
        Assert.Null(result.ProblemCode);
    }
}
