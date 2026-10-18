namespace Conversia.Domain.Enums;

/// <summary>Processing state of a stored webhook delivery. Stored by name.</summary>
public enum WebhookEventStatus
{
    Pending = 0,
    Processed = 1,
    Failed = 2,
}
