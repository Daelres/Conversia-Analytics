namespace Conversia.Domain.Interfaces;

/// <summary>The marks of suppressed contacts, as ingestion reads them (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression").</summary>
public interface IContactSuppressionRepository
{
    /// <summary>When the number was last suppressed in the company; null when it never was. A message sent up to then is dropped.</summary>
    Task<DateTime?> GetSuppressedUntilAsync(Guid tenantId, string waId, CancellationToken cancellationToken = default);
}
