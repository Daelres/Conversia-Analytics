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

/// <summary>
/// The 360dialog adapter for template sends, read receipts, the typing indicator, the window refusal and the template list, against a
/// fake transport. The payload shapes follow the verified documentation (docs/MESSAGING.md, "Verified provider facts").
/// </summary>
public class Dialog360TemplatesAndReceiptsTests
{
    private const string ApiKey = "d360-key-for-tests-Ab3";
    private const string Recipient = "573005550101";

    private readonly Handler _handler = new();
    private readonly Logger _logger = new();
    private readonly Protector _protector = new();

    private sealed class Handler : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

        public List<(HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Answers.Count > 0 ? Answers.Dequeue()() : Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.T-1"}]}""");
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public bool IsConfigured => true;

        public string Protect(string plaintext, string purpose) => $"{purpose}|{plaintext}";

        public string Unprotect(string protectedValue, string purpose) => protectedValue[(purpose.Length + 1)..];
    }

    private sealed class Logger : ILogger<Dialog360MessageSender>, ILogger<Dialog360TemplateSource>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private Dialog360MessageSender Sender() => new(new HttpClient(_handler), _protector, Dialog360ApiOptions.Default, _logger);

    private Dialog360TemplateSource Source() => new(new HttpClient(_handler), _protector, Dialog360ApiOptions.Default, _logger);

    private WhatsAppChannel Channel()
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100");
        channel.SetProviderCredentials(_protector.Protect(ApiKey, ProviderCredentialPurpose.ForChannel(channel.Id)), WhatsAppProviderEnvironment.Production, DateTime.UtcNow);
        return channel;
    }

    // ---- Template send ----------------------------------------------------------------------

    [Fact]
    public async Task Template_PostsTheCloudApiTemplateMessage_WithPositionalBodyParameters()
    {
        var result = await Sender().SendTemplateAsync(new WhatsAppTemplateMessage(
            Channel(), Recipient, "recordatorio_cita", "es_CO", [new(null, "Laura"), new(null, "lunes \"3\" 🙂")]));

        Assert.Equal(WhatsAppSendResult.Sent("wamid.T-1"), result);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://waba-v2.360dialog.io/v1/messages", request.Uri.AbsoluteUri);
        Assert.Equal([ApiKey], request.Headers["D360-API-KEY"]);

        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal(Recipient, root.GetProperty("to").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        var template = root.GetProperty("template");
        Assert.Equal("recordatorio_cita", template.GetProperty("name").GetString());
        Assert.Equal("es_CO", template.GetProperty("language").GetProperty("code").GetString());
        Assert.Equal("deterministic", template.GetProperty("language").GetProperty("policy").GetString());
        var body = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal("body", body.GetProperty("type").GetString());
        var parameters = body.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["Laura", "lunes \"3\" 🙂"], parameters.Select(p => p.GetProperty("text").GetString()));
        Assert.All(parameters, p => Assert.Equal("text", p.GetProperty("type").GetString()));
        Assert.All(parameters, p => Assert.False(p.TryGetProperty("parameter_name", out _)));
    }

    [Fact]
    public async Task Template_NamedParameters_CarryTheirNames()
    {
        await Sender().SendTemplateAsync(new WhatsAppTemplateMessage(Channel(), Recipient, "pedido", "es", [new("first_name", "Laura")]));

        using var json = JsonDocument.Parse(Assert.Single(_handler.Requests).Body);
        var parameter = json.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")[0];
        Assert.Equal("first_name", parameter.GetProperty("parameter_name").GetString());
    }

    [Fact]
    public async Task Template_WithoutParameters_SendsNoComponents()
    {
        await Sender().SendTemplateAsync(new WhatsAppTemplateMessage(Channel(), Recipient, "hola", "es", []));

        using var json = JsonDocument.Parse(Assert.Single(_handler.Requests).Body);
        Assert.False(json.RootElement.GetProperty("template").TryGetProperty("components", out _));
    }

    // ---- The window refusal ----------------------------------------------------------------------

    [Fact]
    public async Task Text_RefusedWithMetasCode131047_IsWindowClosed_AndOnlyTheCodeIsLogged()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#131047) Re-engagement message for 573005550101","type":"OAuthException","code":131047,"error_data":{"details":"Message failed to send because more than 24 hours have passed"}}}"""));

        var result = await Sender().SendTextAsync(new WhatsAppTextMessage(Channel(), Recipient, "Hola"));

        Assert.Equal(WhatsAppSendStatus.WindowClosed, result.Status);
        Assert.Contains(_logger.Lines, l => l.Contains("131047", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Lines, l => l.Contains(Recipient, StringComparison.Ordinal) || l.Contains("Re-engagement", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"error":{"code":131026}}""")]
    [InlineData("""{"meta":{"success":false}}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Text_AnyOther4xx_IsStillRejected(string body)
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.BadRequest, body));

        var result = await Sender().SendTextAsync(new WhatsAppTextMessage(Channel(), Recipient, "Hola"));

        Assert.Equal(WhatsAppSendStatus.Rejected, result.Status);
    }

    // ---- Read receipts and typing ----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_PostsStatusReadWithTheMessageId_AndTheTypingIndicatorOnlyWhenAsked(bool typing)
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, """{"success":true}"""));

        var status = await Sender().MarkReadAsync(new WhatsAppReadReceipt(Channel(), "wamid.IN-9", typing));

        Assert.Equal(WhatsAppSendStatus.Sent, status);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://waba-v2.360dialog.io/v1/messages", request.Uri.AbsoluteUri);
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal(("whatsapp", "read", "wamid.IN-9"), (root.GetProperty("messaging_product").GetString(), root.GetProperty("status").GetString(), root.GetProperty("message_id").GetString()));
        Assert.Equal(typing, root.TryGetProperty("typing_indicator", out var indicator));
        if (typing)
        {
            Assert.Equal("text", indicator.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Read_ARefusal_IsReportedWithoutThrowing()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.BadRequest, """{"error":{"code":131009}}"""));

        Assert.Equal(WhatsAppSendStatus.Rejected, await Sender().MarkReadAsync(new WhatsAppReadReceipt(Channel(), "wamid.IN-9", false)));
    }

    // ---- The template list ----------------------------------------------------------------------

    private static string Page(int total, int offset, params string[] templates)
        => $$"""{"count":{{templates.Length}},"filters":{},"limit":2,"offset":{{offset}},"sort":["id"],"total":{{total}},"waba_templates":[{{string.Join(",", templates)}}]}""";

    private static string Template(string name, string status = "approved", string category = "UTILITY", string components = """[{"type":"BODY","text":"Hola {{1}}"}]""", string language = "es")
        => $$"""{"category":"{{category}}","components":{{components}},"external id":"1","language":"{{language}}","name":"{{name}}","namespace":"ns","rejected_reason":"NONE","status":"{{status}}"}""";

    [Fact]
    public async Task List_ReadsEveryPage_WithTheChannelsKey()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(3, 0, Template("uno"), Template("dos", status: "PAUSED", category: "MARKETING"))));
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(3, 2, Template("tres", status: "submitted"))));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Equal(["uno", "dos", "tres"], result.Templates.Select(t => t.Name));
        Assert.Equal([TemplateStatus.Approved, TemplateStatus.Paused, TemplateStatus.Pending], result.Templates.Select(t => t.Status));
        Assert.Equal(TemplateCategory.Marketing, result.Templates[1].Category);
        Assert.Equal("Hola {{1}}", result.Templates[0].BodyText);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal([ApiKey], r.Headers["D360-API-KEY"]));
        Assert.Equal("/v1/configs/templates", _handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("offset=0", _handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("offset=2", _handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{1}}"}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Hola {{1}}"},{"type":"BODY","text":"Hola"}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Bienvenida"},{"type":"BODY","text":"Hola {{1}}"},{"type":"FOOTER","text":"Gracias"}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola"},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"},{"type":"URL","text":"Ver","url":"https://example.com"}]}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola"},{"type":"BUTTONS","buttons":[{"type":"URL","text":"Ver","url":"https://example.com/{{1}}"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola"},{"type":"BUTTONS","buttons":[{"type":"COPY_CODE","example":"ABC"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola"},{"type":"CAROUSEL","cards":[]}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Sin cuerpo"}]""", false)]
    public async Task List_MarksWhatConversiaCannotSendYet(string components, bool supported)
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(1, 0, Template("plantilla", components: components))));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(supported, Assert.Single(result.Templates).Supported);
    }

    [Fact]
    public async Task List_AnAuthenticationTemplate_IsNotSupported()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(1, 0, Template("otp", category: "AUTHENTICATION"))));

        Assert.False(Assert.Single((await Source().ListAsync(Channel())).Templates).Supported);
    }

    [Fact]
    public async Task List_SkipsEntriesThatAreNotTemplates()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(3, 0, Template("valida"), Template("Con Espacios"), Template("sin_idioma", language: ""))));

        Assert.Equal(["valida"], (await Source().ListAsync(Channel())).Templates.Select(t => t.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WhatsAppSendStatus.InvalidCredentials)]
    [InlineData(HttpStatusCode.TooManyRequests, WhatsAppSendStatus.LimitReached)]
    [InlineData(HttpStatusCode.BadGateway, WhatsAppSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.NotFound, WhatsAppSendStatus.Rejected)]
    public async Task List_ARefusal_ReturnsNoTemplates(HttpStatusCode status, WhatsAppSendStatus expected)
    {
        _handler.Answers.Enqueue(() => Json(status, "{}"));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Templates);
    }

    [Fact]
    public async Task List_AFailureOnALaterPage_FailsTheWholeList()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(3, 0, Template("uno"), Template("dos"))));
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.InternalServerError, "{}"));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(WhatsAppSendStatus.TransientFailure, result.Status);
        Assert.Empty(result.Templates);
    }

    [Fact]
    public async Task List_AnUnreadableAnswer_IsATransientFailure()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, "<html>"));

        Assert.Equal(WhatsAppSendStatus.TransientFailure, (await Source().ListAsync(Channel())).Status);
    }

    [Fact]
    public async Task List_WithoutAKey_Throws()
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100");

        await Assert.ThrowsAsync<MessagingException>(() => Source().ListAsync(channel));
        Assert.Empty(_handler.Requests);
    }

    // ---- Partial reads never pass as a whole list (review fix 1) ----------------------------------------------

    [Fact]
    public async Task List_AnEmptyPageBeforeTheTotal_IsAFailure_NotAShorterList()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(5, 0)));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(WhatsAppSendStatus.TransientFailure, result.Status);
        Assert.Empty(result.Templates);
    }

    [Fact]
    public async Task List_ThatTheProviderSaysIsEmpty_IsCompleteAndEmpty()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(0, 0)));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task List_WithSkippedEntries_IsNotComplete()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(2, 0, Template("Invalida"), Template("valida"))));

        var result = await Source().ListAsync(Channel());

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.False(result.Complete);
        Assert.Single(result.Templates);
    }

    [Fact]
    public async Task List_EveryPageReadWithNothingSkipped_IsComplete()
    {
        _handler.Answers.Enqueue(() => Json(HttpStatusCode.OK, Page(1, 0, Template("valida"))));

        Assert.True((await Source().ListAsync(Channel())).Complete);
    }
}
