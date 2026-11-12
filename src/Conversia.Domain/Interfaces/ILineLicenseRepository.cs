using Conversia.Domain.Entities;
using Conversia.Domain.ReadModels;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// The platform's line licence inventory (platform-level: no tenant filter). Changes that involve a company go through
/// <see cref="ITenantUnitOfWork"/> (the company's row is locked); the licence itself is protected by an optimistic concurrency token, so a
/// write that lost a race throws <see cref="Exceptions.ConcurrencyConflictException"/>.
/// </summary>
public interface ILineLicenseRepository
{
    /// <summary>Tracked; null when it does not exist.</summary>
    Task<LineLicense?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    void Add(LineLicense license);

    /// <summary>
    /// Saves the tracked changes and the audit entry, if any, in one write. A second live licence of the same number throws
    /// <see cref="Exceptions.DuplicateLineLicenseException"/>; a lost race <see cref="Exceptions.ConcurrencyConflictException"/>.
    /// </summary>
    Task SaveAsync(PlatformAuditLog? entry, CancellationToken cancellationToken = default);

    /// <summary>An assigned licence of the company that matches the number (digits or provider id) and is linked to no line; tracked.</summary>
    Task<LineLicense?> FindAssignedMatchAsync(Guid tenantId, string displayPhoneNumber, string? providerPhoneNumberId, CancellationToken cancellationToken = default);

    /// <summary>One licence as the administrator sees it (number masked, company name); null when it does not exist.</summary>
    Task<LineLicenseRow?> GetRowAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ReadPage<LineLicenseRow>> ListAsync(LineLicenseFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}
