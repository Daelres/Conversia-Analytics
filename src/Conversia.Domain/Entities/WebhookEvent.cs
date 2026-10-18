using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// One webhook delivery, stored as received and processed later by a background worker. <c>(ChannelId, BodyHash)</c> is unique,
/// so the provider re-sending the same body stores nothing new. The raw payload holds customers' phone numbers (see
/// docs/WEBHOOKS.md and docs/decisiones.md for the retention open item).
/// </summary>
public class WebhookEvent : BaseEntity
{
    public const int BodyHashLength = 32;
    public const int LastErrorMaxLength = 1000;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    public Guid TenantId { get; private set; }

    public Guid ChannelId { get; private set; }

    /// <summary>SHA-256 of the raw request body.</summary>
    public byte[] BodyHash { get; private set; } = [];

    /// <summary>The body as received (valid JSON, stored as <c>jsonb</c>).</summary>
    public string RawPayload { get; private set; } = string.Empty;

    public DateTime ReceivedAt { get; private set; }

    public WebhookEventStatus Status { get; private set; } = WebhookEventStatus.Pending;

    /// <summary>How many processing attempts failed so far.</summary>
    public int Attempts { get; private set; }

    /// <summary>The event is claimable once this instant has passed.</summary>
    public DateTime NextAttemptAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Entries of the delivery that looked like customer messages but could not be stored (no usable id, phone, timestamp...). Null until processed.</summary>
    public int? SkippedMessageCount { get; private set; }

    /// <summary>Delivery receipts (<c>statuses</c>) in the delivery. Null until processed.</summary>
    public int? StatusCount { get; private set; }

    // Required by EF Core for materialization.
    private WebhookEvent()
    {
    }

    public static WebhookEvent Receive(Guid tenantId, Guid channelId, byte[] bodyHash, string rawPayload, DateTime receivedAt)
    {
        if (bodyHash is not { Length: BodyHashLength })
        {
            throw new DomainException($"El hash del cuerpo debe tener {BodyHashLength} bytes.");
        }

        if (string.IsNullOrEmpty(rawPayload))
        {
            throw new DomainException("El cuerpo del evento es obligatorio.");
        }

        var received = UtcTime.Normalize(receivedAt);
        return new WebhookEvent
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            BodyHash = bodyHash,
            RawPayload = rawPayload,
            ReceivedAt = received,
            NextAttemptAt = received,
        };
    }

    public void RecordParseCounts(int skippedMessages, int statuses)
    {
        SkippedMessageCount = skippedMessages;
        StatusCount = statuses;
    }

    public void MarkProcessed(DateTime now)
    {
        Status = WebhookEventStatus.Processed;
        ProcessedAt = UtcTime.Normalize(now);
        LastError = null;
    }

    /// <summary>
    /// Counts a failed attempt. Below <paramref name="maxAttempts"/> the event stays pending and becomes claimable again after an
    /// exponential delay; at the limit it is <see cref="WebhookEventStatus.Failed"/> and nothing retries it.
    /// </summary>
    /// <param name="sanitizedError">Already sanitized by the caller; cut to <see cref="LastErrorMaxLength"/> here.</param>
    public void RegisterFailure(string sanitizedError, DateTime now, int maxAttempts, TimeSpan baseDelay)
    {
        if (maxAttempts < 1)
        {
            throw new DomainException("El máximo de intentos debe ser al menos 1.");
        }

        Attempts++;
        LastError = sanitizedError.Length <= LastErrorMaxLength ? sanitizedError : sanitizedError[..LastErrorMaxLength];

        if (Attempts >= maxAttempts)
        {
            Status = WebhookEventStatus.Failed;
            return;
        }

        NextAttemptAt = UtcTime.Normalize(now) + BackoffDelay(Attempts, baseDelay);
    }

    /// <summary><paramref name="baseDelay"/> doubled after each failed attempt (the first retry waits the base), capped at six hours.</summary>
    public static TimeSpan BackoffDelay(int failedAttempts, TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var seconds = baseDelay.TotalSeconds * Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 30));
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }
}
