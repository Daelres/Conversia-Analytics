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

    /// <summary>How many processing attempts were started so far (counted when the event is claimed, so an attempt that kills the worker still counts).</summary>
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
    /// A worker claims the event: the attempt is counted now, before any work, and the event is leased until
    /// <paramref name="lease"/> has passed: nobody else can claim it meanwhile, and if the worker dies the event becomes claimable again
    /// and the attempt stays counted, so a payload that kills the process ends up <see cref="WebhookEventStatus.Failed"/>.
    /// </summary>
    public void BeginAttempt(DateTime now, TimeSpan lease)
    {
        Attempts++;
        NextAttemptAt = UtcTime.Normalize(now) + lease;
    }

    /// <summary>The event could not be worked on yet (its tenant is busy): the attempt is given back and the event waits <paramref name="delay"/>.</summary>
    public void Defer(DateTime now, TimeSpan delay)
    {
        Attempts = Math.Max(0, Attempts - 1);
        NextAttemptAt = UtcTime.Normalize(now) + delay;
    }

    /// <summary>
    /// Records that the attempt begun by <see cref="BeginAttempt"/> failed. Below <paramref name="maxAttempts"/> the event stays pending
    /// and becomes claimable again after an exponential delay; at the limit it is <see cref="WebhookEventStatus.Failed"/> and nothing retries it.
    /// </summary>
    /// <param name="sanitizedError">Already sanitized by the caller; cut to <see cref="LastErrorMaxLength"/> here.</param>
    public void RegisterFailure(string sanitizedError, DateTime now, int maxAttempts, TimeSpan baseDelay)
    {
        if (maxAttempts < 1)
        {
            throw new DomainException("El máximo de intentos debe ser al menos 1.");
        }

        LastError = Cut(sanitizedError);

        if (Attempts >= maxAttempts)
        {
            Status = WebhookEventStatus.Failed;
            return;
        }

        NextAttemptAt = UtcTime.Normalize(now) + BackoffDelay(Attempts, baseDelay);
    }

    /// <summary>A failure no retry can fix (for example a delivery over the size cap): straight to <see cref="WebhookEventStatus.Failed"/>.</summary>
    public void Fail(string sanitizedError)
    {
        LastError = Cut(sanitizedError);
        Status = WebhookEventStatus.Failed;
    }

    /// <summary>Puts a <see cref="WebhookEventStatus.Failed"/> event back in the queue with a fresh budget of attempts.</summary>
    public void Requeue(DateTime now)
    {
        if (Status != WebhookEventStatus.Failed)
        {
            throw new DomainException("Solo se puede reencolar un evento fallido.");
        }

        Status = WebhookEventStatus.Pending;
        Attempts = 0;
        NextAttemptAt = UtcTime.Normalize(now);
        ProcessedAt = null;
        LastError = null;
    }

    private static string Cut(string text) => TextRules.Truncate(text, LastErrorMaxLength);

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
