using System.Net;
using System.Net.Http.Headers;
using Conversia.Application.DTOs.Conversations;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>
/// Media in the conversation page (docs/MESSAGING.md, "Media"): images inline, audio and video players, document links, the state of a file
/// that is not stored, the Web's pass-through of the file (the browser never talks to the API or the store) and the attachment composer.
/// Against a stand-in API; the bytes are synthetic.
/// </summary>
public class ConversationMediaWebTests
{
    private static readonly Guid Conversation = ConversationsStub.Won;

    /// <summary>An open conversation of the stub (the composers are offered).</summary>
    private static readonly Guid Open = ConversationsStub.Manual;
    private static readonly byte[] Pdf = "%PDF-1.4\nsintetico\n%%EOF\n"u8.ToArray();

    private static MessageResponse Media(string type, string status, string? contentType = null, string direction = "Inbound", string? text = null)
        => new()
        {
            Id = Guid.NewGuid(), Direction = direction, Type = type, Text = text, SentAt = DateTime.UtcNow.AddMinutes(-5),
            Attachment = new MessageAttachmentResponse { Status = status, Kind = type, ContentType = contentType, SizeBytes = contentType is null ? null : 1234 },
        };

    private static ConversationsHarness With(params MessageResponse[] messages) => ConversationsHarness.Create(configure: stub => stub.Messages = messages);

    private static string FileUrl(Guid messageId) => $"/conversaciones/{Conversation}/adjuntos/{messageId}";

    [Fact]
    public async Task AStoredImage_IsShownInline_ThroughTheWeb_WithBoundedSize()
    {
        var image = Media("image", "stored", "image/jpeg", text: "Así quedó");
        using var harness = With(image);

        var page = (await harness.OpenAsync($"/conversaciones/{Conversation}")).ShouldBe(HttpStatusCode.OK);

        var img = Assert.Single(page.Select("[data-testid=chat-media] img"));
        Assert.Equal(FileUrl(image.Id), img.GetAttribute("src"));
        Assert.Equal("lazy", img.GetAttribute("loading"));
        Assert.Contains("chat__image", img.ClassName);
        Assert.False(string.IsNullOrEmpty(img.GetAttribute("alt")));
        Assert.Contains("Así quedó", page.Text);
    }

    [Theory]
    [InlineData("audio", "audio/ogg", "audio")]
    [InlineData("video", "video/mp4", "video")]
    public async Task AudioAndVideo_GetAPlayer_ThatLoadsNothingUntilPlayed(string type, string contentType, string element)
    {
        var media = Media(type, "stored", contentType);
        using var harness = With(media);

        var page = await harness.OpenAsync($"/conversaciones/{Conversation}");

        var player = Assert.Single(page.Select($"[data-testid=chat-media] {element}"));
        Assert.True(player.HasAttribute("controls"));
        Assert.Equal("none", player.GetAttribute("preload"));
        Assert.Equal(FileUrl(media.Id), player.QuerySelector("source")!.GetAttribute("src"));
    }

    [Fact]
    public async Task ADocument_IsALinkToDownload_NotShown()
    {
        var document = Media("document", "stored", "application/pdf");
        using var harness = With(document);

        var page = await harness.OpenAsync($"/conversaciones/{Conversation}");

        var link = Assert.Single(page.Select("[data-testid=chat-media] a"));
        Assert.Equal(FileUrl(document.Id), link.GetAttribute("href"));
        Assert.True(link.HasAttribute("download"));
        Assert.Contains("PDF", link.TextContent);
        Assert.Empty(page.Select("[data-testid=chat-media] img, [data-testid=chat-media] iframe, [data-testid=chat-media] object"));
    }

    [Theory]
    [InlineData("pending", "Descargando")]
    [InlineData("too_large", "demasiado grande")]
    [InlineData("blocked", "no está permitido")]
    [InlineData("expired", "ya no está disponible")]
    [InlineData("failed", "No se pudo descargar")]
    [InlineData("purged", "se eliminó")]
    public async Task AFileThatIsNotStored_SaysWhy_AndLinksToNothing(string status, string words)
    {
        using var harness = With(Media("image", status));

        var page = await harness.OpenAsync($"/conversaciones/{Conversation}");

        var media = Assert.Single(page.Select("[data-testid=chat-media]"));
        Assert.Equal(status, media.GetAttribute("data-status"));
        Assert.Contains(words, media.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(media.QuerySelectorAll("a, img, audio, video"));
    }

    [Fact]
    public async Task TheFile_IsPassedThrough_WithTheApisSafeHeaders_AndTheRange()
    {
        var document = Media("document", "stored", "application/pdf");
        using var harness = With(document);
        harness.Stub.Overrides[$"GET /api/conversations/{Conversation}/messages/{document.Id}/media"] = request =>
        {
            var response = new HttpResponseMessage(request.Header("Range") is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Pdf) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "documento.pdf" };
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        };
        using var session = await harness.SignInAsync();

        var (file, bytes) = await session.GetFileAsync(FileUrl(document.Id));

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Pdf, bytes);
        Assert.StartsWith("application/pdf", file.Header("Content-Type"));
        Assert.StartsWith("attachment", file.Header("Content-Disposition"));
        Assert.Equal("nosniff", file.Header("X-Content-Type-Options"));
        Assert.Contains("sandbox", file.Header("Content-Security-Policy"));
        Assert.DoesNotContain("public", file.Header("Cache-Control") ?? string.Empty);
        var call = Assert.Single(harness.Backend.DataRequests, r => r.Path.EndsWith("/media", StringComparison.Ordinal));
        Assert.StartsWith("Bearer ", call.Header("Authorization"));
    }

    [Fact]
    public async Task AFileTheApiDoesNotHave_IsA404()
    {
        using var harness = With();
        var unknown = Guid.NewGuid();
        harness.Stub.Overrides[$"GET /api/conversations/{Conversation}/messages/{unknown}/media"] = _ => FakeBackend.Raw(HttpStatusCode.NotFound, null);
        using var session = await harness.SignInAsync();

        var (file, _) = await session.GetFileAsync(FileUrl(unknown));

        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }

    [Fact]
    public async Task WithoutASession_TheFileIsNotServed()
    {
        using var harness = With();
        using var anonymous = harness.Host.NewSession();

        var (file, _) = await anonymous.GetFileAsync(FileUrl(Guid.NewGuid()));

        Assert.NotEqual(HttpStatusCode.OK, file.StatusCode);
        Assert.DoesNotContain(harness.Backend.DataRequests, r => r.Path.EndsWith("/media", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAttachmentComposer_IsAMultipartForm_ForImagesAndDocuments()
    {
        using var harness = With();

        var page = await harness.OpenAsync($"/conversaciones/{Open}");

        var form = page.FindForm(f => f.GetAttribute("data-testid") == "attachment-form", "attachment form");
        Assert.Equal("multipart/form-data", form.GetAttribute("enctype"));
        Assert.EndsWith($"/conversaciones/{Open}/adjuntar", form.GetAttribute("action"));
        var input = form.QuerySelector("input[type=file]")!;
        Assert.Contains("image/jpeg", input.GetAttribute("accept"));
        Assert.Contains("application/pdf", input.GetAttribute("accept"));
        Assert.NotNull(form.QuerySelector("input[name=__RequestVerificationToken]"));
        Assert.Empty(page.Select("[data-testid=attachment-composer] script")); // no inline script: the CSP of the pages stays as it is
    }

    [Fact]
    public async Task SendingAnAttachment_PostsItToTheApi_WithoutTheFileName_AndSaysItWasSent()
    {
        using var harness = With();
        harness.Stub.Overrides[$"POST /api/conversations/{Open}/media-messages"] = _ => FakeBackend.Answer(HttpStatusCode.Created, Media("document", "stored", "application/pdf", "Outbound"));
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync($"/conversaciones/{Open}");
        var token = page.FindForm(f => f.GetAttribute("data-testid") == "attachment-form", "attachment form")
            .QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Tu cotización"), "caption" },
        };
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "cotizacion-laura-3005550101.pdf");
        var answer = await session.PostRawAsync($"/conversaciones/{Open}/adjuntar", form);

        Assert.True(answer.IsRedirect, answer.Status.ToString());
        var call = Assert.Single(harness.Backend.DataRequests, r => r.Path.EndsWith("/media-messages", StringComparison.Ordinal));
        Assert.Contains("Tu cotización", call.Body);
        Assert.DoesNotContain("cotizacion-laura", call.Body);
        var after = await session.FollowAsync(answer);
        Assert.Contains("El archivo se envió", after.Text);
    }

    [Fact]
    public async Task ARefusedAttachment_HasItsOwnWording()
    {
        using var harness = With();
        harness.Stub.Overrides[$"POST /api/conversations/{Open}/media-messages"] = _ => FakeBackend.Raw(HttpStatusCode.UnsupportedMediaType, """{"title":"x","status":415,"detail":"x","code":"media_type_not_allowed"}""");
        using var session = await harness.SignInAsync();
        var page = await session.GetAsync($"/conversaciones/{Open}");
        var token = page.FindForm(f => f.GetAttribute("data-testid") == "attachment-form", "attachment form")
            .QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" }, { new ByteArrayContent([1, 2, 3]), "file", "x.bin" } };

        var after = await session.FollowAsync(await session.PostRawAsync($"/conversaciones/{Open}/adjuntar", form));

        Assert.Contains("solo se admiten imágenes JPEG o PNG", after.Text);
    }
}
