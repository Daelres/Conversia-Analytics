using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.UnitTests.Messaging;

/// <summary>The 360dialog adapter against a fake transport: the request it builds, how each answer is read, and what it never logs.</summary>
public class Dialog360MessageSenderTests
{
    private const string ApiKey = "d360-key-for-tests-Ab3";
    private const string Recipient = "573005550101";
    private const string AcceptedBody = """{"messaging_product":"whatsapp","contacts":[{"input":"573005550101","wa_id":"573005550101"}],"messages":[{"id":"wamid.HBgM-SENT-1"}]}""";

    private readonly RecordingHandler _handler = new();
    private readonly RecordingLogger _logger = new();
    private readonly PrefixProtector _protector = new();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
            = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, AcceptedBody));

        public List<(HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, string? ContentType, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await Respond(request, cancellationToken);
        }
    }

    /// <summary>Stands in for the real protector: the stored value is the key behind a marker and the purpose it was stored for.</summary>
    private sealed class PrefixProtector : ISecretProtector
    {
        public SecretProtectionFailure? Fails { get; set; }

        public bool IsConfigured => true;

        public string Protect(string plaintext, string purpose) => $"{purpose}|{plaintext}";

        public string Unprotect(string protectedValue, string purpose)
        {
            if (Fails is { } failure)
            {
                throw new SecretProtectionException(failure);
            }

            var prefix = purpose + "|";
            return protectedValue.StartsWith(prefix, StringComparison.Ordinal)
                ? protectedValue[prefix.Length..]
                : throw new SecretProtectionException(SecretProtectionFailure.Unreadable);
        }
    }

    private sealed class RecordingLogger : ILogger<Dialog360MessageSender>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : "\n" + exception)}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private Dialog360MessageSender CreateSut(Dialog360ApiOptions? options = null)
        => new(new HttpClient(_handler), _protector, options ?? Dialog360ApiOptions.Default, _logger);

    private WhatsAppChannel Channel(WhatsAppProviderEnvironment environment = WhatsAppProviderEnvironment.Sandbox)
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100");
        channel.SetProviderCredentials(_protector.Protect(ApiKey, ProviderCredentialPurpose.ForChannel(channel.Id)), environment, DateTime.UtcNow);
        return channel;
    }

    private Task<WhatsAppSendResult> SendAsync(WhatsAppChannel? channel = null, string text = "Hola Laura", CancellationToken cancellationToken = default)
        => CreateSut().SendTextAsync(new WhatsAppTextMessage(channel ?? Channel(), Recipient, text), cancellationToken);

    // ---- The request -------------------------------------------------------------------------

    [Fact]
    public async Task Send_PostsTheCloudApiTextMessage_WithTheChannelsKeyInTheHeader()
    {
        await SendAsync(text: "Hola \"Laura\"\n¿cómo estás? 🙂");

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://waba-sandbox.360dialog.io/v1/messages", request.Uri.AbsoluteUri);
        Assert.Equal([ApiKey], request.Headers["D360-API-KEY"]);
        Assert.Equal("application/json", request.ContentType);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("individual", root.GetProperty("recipient_type").GetString());
        Assert.Equal(Recipient, root.GetProperty("to").GetString());
        Assert.Equal("text", root.GetProperty("type").GetString());
        Assert.Equal("Hola \"Laura\"\n¿cómo estás? 🙂", root.GetProperty("text").GetProperty("body").GetString());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(WhatsAppProviderEnvironment.Sandbox, "https://waba-sandbox.360dialog.io/v1/messages")]
    [InlineData(WhatsAppProviderEnvironment.Production, "https://waba-v2.360dialog.io/v1/messages")]
    public async Task Send_GoesToTheConfiguredBaseUrlOfTheChannelsEnvironment(WhatsAppProviderEnvironment environment, string expected)
    {
        await SendAsync(Channel(environment));

        Assert.Equal(expected, Assert.Single(_handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Send_UsesTheBaseUrlsOfTheOptions_WithOrWithoutATrailingSlash()
    {
        var options = new Dialog360ApiOptions(new Uri("https://sandbox.example.test/prefix"), new Uri("https://production.example.test/"), 15);

        await CreateSut(options).SendTextAsync(new WhatsAppTextMessage(Channel(), Recipient, "Hola"));
        await CreateSut(options).SendTextAsync(new WhatsAppTextMessage(Channel(WhatsAppProviderEnvironment.Production), Recipient, "Hola"));

        Assert.Equal("https://sandbox.example.test/prefix/v1/messages", _handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("https://production.example.test/v1/messages", _handler.Requests[1].Uri.AbsoluteUri);
    }

    // ---- Reading the answer ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Send_TreatsAny2xxAsSent_AndReadsTheMessageId(HttpStatusCode status)
    {
        _handler.Respond = (_, _) => Task.FromResult(Json(status, AcceptedBody));

        var result = await SendAsync();

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Equal("wamid.HBgM-SENT-1", result.ProviderMessageId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"messages":"none"}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[{}]}""")]
    [InlineData("""{"messages":[{"id":42}]}""")]
    [InlineData("""{"messages":[{"id":"   "}]}""")]
    [InlineData("""{"messages":["wamid.X"]}""")]
    public async Task Send_WhenA2xxCarriesNoUsableId_IsStillSent_WithoutAnId(string body)
    {
        _handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, body));

        var result = await SendAsync();

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Send_IgnoresAnIdTooLongToStore()
    {
        var id = new string('w', Message.ProviderMessageIdMaxLength + 1);
        _handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, $$"""{"messages":[{"id":"{{id}}"}]}"""));

        Assert.Null((await SendAsync()).ProviderMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WhatsAppSendStatus.InvalidCredentials)]
    [InlineData(HttpStatusCode.Forbidden, WhatsAppSendStatus.RecipientNotAllowed)]
    [InlineData(HttpStatusCode.TooManyRequests, WhatsAppSendStatus.LimitReached)]
    [InlineData(HttpStatusCode.BadRequest, WhatsAppSendStatus.Rejected)]
    [InlineData(HttpStatusCode.NotFound, WhatsAppSendStatus.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, WhatsAppSendStatus.Rejected)]
    [InlineData(HttpStatusCode.RequestTimeout, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.BadGateway, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.GatewayTimeout, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.Found, WhatsAppSendStatus.TransientFailure)] // a redirect is never followed with the key
    public async Task Send_MapsEachRefusalToItsOutcome_WithoutAnId(HttpStatusCode status, WhatsAppSendStatus expected)
    {
        _handler.Respond = (_, _) => Task.FromResult(Json(status, """{"error":{"message":"details the caller must not see"},"messages":[{"id":"wamid.NOT-SENT"}]}"""));

        var result = await SendAsync();

        Assert.Equal(expected, result.Status);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Send_DoesNotRetry_APostIsNotIdempotent()
    {
        _handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.BadGateway, "{}"));

        await SendAsync();

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Send_WhenTheProviderCannotBeReached_IsATransientFailure()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");

        Assert.Equal(WhatsAppSendStatus.TransientFailure, (await SendAsync()).Status);
    }

    [Fact]
    public async Task Send_WhenTheProviderTimesOut_IsATransientFailure()
    {
        // What HttpClient throws for its own timeout: a cancellation the caller did not ask for.
        _handler.Respond = (_, _) => throw new TaskCanceledException("timeout", new TimeoutException());

        Assert.Equal(WhatsAppSendStatus.TransientFailure, (await SendAsync()).Status);
    }

    [Fact]
    public async Task Send_WhenTheCallerCancels_IsCancelled_NotAProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.Respond = async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.Created, AcceptedBody);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendAsync(cancellationToken: cancellation.Token));
    }

    // ---- After a 2xx the message has left: nothing may turn that into a failure -------------------

    /// <summary>A body that announces it is being read and then never arrives (until the read itself is cancelled).</summary>
    private sealed class StalledContent(TaskCompletionSource reading) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new StalledStream(reading));

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream(reading));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task Send_WhenTheBodyOfA2xxNeverArrives_IsSentWithoutAnId_AfterAShortWait()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StalledContent(reading) });
        var sut = new Dialog360MessageSender(new HttpClient(_handler), _protector, Dialog360ApiOptions.Default, _logger) { BodyReadTimeout = TimeSpan.FromMilliseconds(150) };

        var send = sut.SendTextAsync(new WhatsAppTextMessage(Channel(), Recipient, "Hola"));
        var result = await send.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reading.Task.IsCompleted);
        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Send_WhenTheCallerCancelsWhileTheBodyOfA2xxIsRead_IsStillSent_SoTheMessageGetsRecorded()
    {
        using var caller = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StalledContent(reading) });
        var sut = new Dialog360MessageSender(new HttpClient(_handler), _protector, Dialog360ApiOptions.Default, _logger) { BodyReadTimeout = TimeSpan.FromMilliseconds(300) };

        var send = sut.SendTextAsync(new WhatsAppTextMessage(Channel(), Recipient, "Hola"), caller.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Send_WhenTheCallerCancelsRightAfterA2xxWithABody_StillReadsTheId()
    {
        using var caller = new CancellationTokenSource();
        _handler.Respond = async (_, _) =>
        {
            await caller.CancelAsync(); // the provider has answered; the caller goes away before the body is read
            return Json(HttpStatusCode.Created, AcceptedBody);
        };

        var result = await SendAsync(cancellationToken: caller.Token);

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Equal("wamid.HBgM-SENT-1", result.ProviderMessageId);
    }

    [Fact]
    public void TheBodyOfA2xx_IsWaitedForTwoSecondsByDefault()
        => Assert.Equal(TimeSpan.FromSeconds(2), CreateSut().BodyReadTimeout);

    // ---- Credentials -------------------------------------------------------------------------

    [Fact]
    public async Task Send_WithoutStoredCredentials_IsRefusedBeforeAnyRequest()
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100");

        var failure = await Assert.ThrowsAsync<MessagingException>(() => SendAsync(channel));

        Assert.Equal(MessagingProblem.CredentialsMissing, failure.Problem);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(SecretProtectionFailure.NotConfigured)]
    [InlineData(SecretProtectionFailure.Unreadable)]
    public async Task Send_WhenTheStoredKeyCannotBeRead_FailsBeforeAnyRequest(SecretProtectionFailure failure)
    {
        _protector.Fails = failure;

        var thrown = await Assert.ThrowsAsync<SecretProtectionException>(() => SendAsync());

        Assert.Equal(failure, thrown.Failure);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Send_ReadsTheKeyStoredForThisChannelOnly()
    {
        var channel = Channel();
        var other = Channel();
        other.SetProviderCredentials(channel.ProviderApiKeyCiphertext!, WhatsAppProviderEnvironment.Sandbox, DateTime.UtcNow);

        await Assert.ThrowsAsync<SecretProtectionException>(() => SendAsync(other));

        Assert.Empty(_handler.Requests);
    }

    // ---- Logs --------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Send_NeverLogsTheKeyTheRecipientTheTextOrTheProvidersBody(HttpStatusCode status)
    {
        const string providerDetail = "provider-error-detail-7781";
        var channel = Channel();
        _handler.Respond = (_, _) => Task.FromResult(Json(status, $$"""{"error":"{{providerDetail}} {{ApiKey}}","messages":[{"id":"wamid.X"}]}"""));

        await SendAsync(channel, text: "texto-privado-del-cliente");
        _handler.Respond = (_, _) => throw new HttpRequestException($"failed with {ApiKey} for {Recipient}");
        await SendAsync(channel, text: "texto-privado-del-cliente");

        var log = string.Join('\n', _logger.Lines);
        foreach (var secret in new[] { ApiKey, channel.ProviderApiKeyCiphertext!, Recipient, "texto-privado-del-cliente", providerDetail })
        {
            Assert.DoesNotContain(secret, log);
        }
    }

    [Fact]
    public async Task Send_LogsARefusalWithItsStatusAndTheChannel_SoItCanBeDiagnosed()
    {
        var channel = Channel();
        _handler.Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));

        await SendAsync(channel);

        var line = Assert.Single(_logger.Lines);
        Assert.StartsWith("Warning", line, StringComparison.Ordinal);
        Assert.Contains("401", line);
        Assert.Contains(channel.Id.ToString(), line);
    }

    [Fact]
    public void TheMessage_DoesNotPrintItsRecipientOrText()
    {
        var printed = new WhatsAppTextMessage(Channel(), Recipient, "texto-privado-del-cliente").ToString();

        Assert.DoesNotContain(Recipient, printed);
        Assert.DoesNotContain("texto-privado", printed);
    }
}
