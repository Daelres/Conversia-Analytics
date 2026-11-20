using Conversia.Domain.Common;

namespace Conversia.Domain.Entities;

/// <summary>
/// The mark a contact suppression leaves (Ley 1581, the customer asked to delete their data; docs/USERS_TENANTS_PERMISSIONS.md, "Contact
/// suppression"): a keyed, per-company hash of the WhatsApp number and the moment of the suppression. Nothing else of the contact is kept. Ingestion
/// drops any message of that number sent up to <see cref="SuppressedAt"/> (an old delivery still queued, or re-sent by the provider), so the
/// suppressed contact is never recreated from its old messages; a message sent after it opens a new contact. Deleted with the company.
/// </summary>
public class ContactSuppression : BaseEntity
{
    public const int HashLength = 64;

    public Guid TenantId { get; private set; }

    /// <summary>HMAC-SHA256 (hex) of the company id and the normalized WhatsApp id with the deployment's privacy key (<c>Privacy:HashKey</c>).</summary>
    public string WaIdHash { get; private set; } = string.Empty;

    /// <summary>The id of the privacy key the hash was made with: a rotated key keeps the old marks readable (docs/USERS_TENANTS_PERMISSIONS.md).</summary>
    public string HashKeyId { get; private set; } = string.Empty;

    public const int HashKeyIdMaxLength = 32;

    public DateTime SuppressedAt { get; private set; }

    /// <summary>The company user who suppressed it.</summary>
    public Guid SuppressedBy { get; private set; }

    // Required by EF Core for materialization.
    private ContactSuppression()
    {
    }

    public static ContactSuppression Create(Guid tenantId, string waIdHash, string hashKeyId, Guid suppressedBy, DateTime now) => new()
    {
        TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria."),
        WaIdHash = KeyedHashes.Required(waIdHash),
        HashKeyId = KeyedHashes.RequiredKeyId(hashKeyId),
        SuppressedBy = TextRules.RequiredId(suppressedBy, "El usuario es obligatorio."),
        SuppressedAt = UtcTime.Normalize(now),
    };

    /// <summary>Whether a message sent at <paramref name="sentAt"/> belongs to the suppressed contact (sent no later than the suppression).</summary>
    public bool Covers(DateTime sentAt) => UtcTime.Normalize(sentAt) <= SuppressedAt;

    /// <summary>The number was suppressed again: the cutoff moves forward, never back.</summary>
    public void Renew(DateTime now)
    {
        var at = UtcTime.Normalize(now);
        if (at > SuppressedAt)
        {
            SuppressedAt = at;
        }
    }
}
