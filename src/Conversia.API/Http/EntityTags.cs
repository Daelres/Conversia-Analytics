using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Conversia.API.Http;

/// <summary>
/// Strong, opaque entity tags for the polled endpoints (docs/MESSAGING.md, "Inbox"): a poll that finds nothing new gets a bodiless
/// <c>304</c>. The tag is a hash of what the answer depends on, never of anything secret, and says nothing readable.
/// </summary>
public static class EntityTags
{
    public static string Of(string fingerprint)
        => $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..32]}\"";

    /// <summary><c>304</c> when the request's <c>If-None-Match</c> names <paramref name="tag"/>; otherwise <c>200</c> with the body. Both carry the tag.</summary>
    public static IActionResult Answer(ControllerBase controller, string tag, object body)
    {
        var response = controller.Response;
        response.Headers.ETag = tag;
        response.Headers.CacheControl = "no-cache";

        var ifNoneMatch = controller.Request.Headers.IfNoneMatch;
        if (EntityTagHeaderValue.TryParseList(ifNoneMatch, out var tags) && tags.Any(t => t.Tag.Equals(tag, StringComparison.Ordinal) || t.Tag == "*"))
        {
            return controller.StatusCode(StatusCodes.Status304NotModified);
        }

        return controller.Ok(body);
    }
}
