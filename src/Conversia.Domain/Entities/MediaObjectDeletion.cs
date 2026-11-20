using Conversia.Domain.Common;

namespace Conversia.Domain.Entities;

/// <summary>
/// A stored media object whose message is gone (a suppressed contact, deleted sandbox data): written in the same transaction that deletes the
/// rows, so the object is deleted from the store afterwards even if that first attempt fails (docs/MESSAGING.md, "Media").
/// </summary>
public class MediaObjectDeletion : BaseEntity
{
    public Guid TenantId { get; private set; }

    public string ObjectKey { get; private set; } = string.Empty;

    /// <summary>Failed attempts to delete the object: a key the store keeps refusing waits longer each time and never blocks the queue.</summary>
    public int Attempts { get; private set; }

    /// <summary>When it may be tried again; null for "now".</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>The store refused it: tried again after a doubling wait (1 minute, 2, 4, ... at most a day).</summary>
    public void Postpone(DateTime now)
    {
        Attempts++;
        NextAttemptAt = UtcTime.Normalize(now).AddMinutes(Math.Min(Math.Pow(2, Attempts - 1), 1440));
    }

    private MediaObjectDeletion()
    {
    }

    public static MediaObjectDeletion Create(Guid tenantId, string objectKey, DateTime now)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            ObjectKey = StoredMediaObject.IsValidKey(objectKey) ? objectKey : throw new Exceptions.DomainException(Message.MediaRuleMessage),
            CreatedAt = UtcTime.Normalize(now),
        };
}
