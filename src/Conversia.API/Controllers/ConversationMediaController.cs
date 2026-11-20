using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Media;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Conversia.API.Controllers;

/// <summary>
/// The media of a conversation (docs/MESSAGING.md, "Media"): the stored copy of a message's file, streamed to an authenticated user of the
/// company (never a URL of the store), and sending an attachment.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationMediaController(IMessageMediaService media, IMessageSendingService messages) : ControllerBase
{
    /// <summary>The largest body of an attachment send: Meta's document limit and room for the form around it.</summary>
    public const long MaxUploadBodyBytes = 100L * 1024 * 1024 + 64 * 1024;

    /// <summary>
    /// The file. Images, audio and video are shown inline; documents are always downloaded (<c>Content-Disposition: attachment</c>) under a
    /// generic name. <c>X-Content-Type-Options: nosniff</c>, a sandboxing CSP and <c>Cache-Control: private</c>; range requests are honoured
    /// (audio and video seek).
    /// </summary>
    [HttpGet("{id:guid}/messages/{messageId:guid}/media")]
    [Authorize(Policy = nameof(Permission.ConversationsView))]
    public async Task<IActionResult> GetMedia(Guid id, Guid messageId, CancellationToken cancellationToken)
    {
        var download = await media.OpenAsync(id, messageId, cancellationToken);
        HttpContext.Response.RegisterForDisposeAsync(download.Content);
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";        
        Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
        Response.Headers[HeaderNames.ContentSecurityPolicy] = "default-src 'none'; sandbox";
        if (download.AsAttachment)
        {
            Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileName = download.FileName }.ToString();
        }

        return File(download.Content.Content, download.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Sends an image or a document: multipart <c>file</c> (required) and <c>caption</c> (optional, 1024 characters). <c>201</c> the stored message.</summary>
    [HttpPost("{id:guid}/media-messages")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    [EnableRateLimiting(MessageSendRateLimitingExtensions.PolicyName)]
    [RequestSizeLimit(MaxUploadBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBodyBytes)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<MessageResponse>> SendMedia(Guid id, [FromForm] IFormFile? file, [FromForm] string? caption, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError("file", "Adjunta un archivo.");
            return ValidationProblem(ModelState);
        }

        // Decided from the first bytes before anything else happens: a type that is not sent, or a file over its kind's limit, is refused
        // without copying the file anywhere (the form file is already buffered by the server, on disk above 64 KB).
        await using var content = file.OpenReadStream();
        var upload = MediaPolicy.InspectUpload(file.ContentType, MediaPolicy.ReadHead(content));
        if (!upload.Allowed)
        {
            throw new MessagingException(MessagingProblem.MediaTypeNotAllowed);
        }

        if (file.Length > MediaPolicy.MaxBytes(upload.Kind))
        {
            throw new MessagingException(MessagingProblem.MediaTooLarge);
        }

        var request = new SendMediaMessageRequest { Content = content, DeclaredContentType = file.ContentType, Caption = caption };
        return StatusCode(StatusCodes.Status201Created, await messages.SendMediaAsync(id, request, User.GetUserId(), cancellationToken));
    }
}
