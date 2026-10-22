using System.Text.RegularExpressions;
using Conversia.Application.ErrorLogging;
using Conversia.Domain.Entities;

namespace Conversia.Application.Ingestion;

/// <summary>A delivery no retry can fix (for example over the size cap). The worker marks the event Failed at once.</summary>
public sealed class WebhookPermanentFailureException(string message) : Exception(message);

/// <summary>The most messages one delivery may hold; a bigger one is a permanent failure. The processing is bounded by it, not by hope.</summary>
public sealed record WebhookIngestionLimits(int MaxMessagesPerDelivery);

/// <summary>
/// The one description of an ingestion failure that may be stored (<c>WebhookEvents.LastError</c>, the error registry) or logged: the
/// exception type and its innermost message, sanitized like every stored error (secrets redacted, control characters stripped, cut) and with
/// digit runs of 7 or more masked, because a database error can quote values such as phone numbers. The raw exception is never logged.
/// </summary>
public static partial class WebhookErrors
{
    public static string Describe(Exception exception)
    {
        var innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        var text = $"{exception.GetType().Name}: {innermost.Message}";
        return ErrorSanitizer.SanitizeText(LongNumber().Replace(text, "[num]"), WebhookEvent.LastErrorMaxLength) ?? string.Empty;
    }

    /// <summary>The same sanitizing for a text that was already stored: applied again on the way out, it changes nothing that is already clean.</summary>
    public static string SanitizeStored(string text)
        => ErrorSanitizer.SanitizeText(LongNumber().Replace(text, "[num]"), WebhookEvent.LastErrorMaxLength) ?? string.Empty;

    [GeneratedRegex(@"\+?\d{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();
}
