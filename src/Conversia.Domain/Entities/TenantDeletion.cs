using System.Text.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The tombstone of a company whose data was deleted (Ley 1581; docs/PLATFORM_ADMIN.md, "Deleting a company's data"): its id, a keyed hash of
/// its name (never the name: data minimisation), the dates, who or
/// what asked for it and how many rows were deleted per table. Written when the deletion starts and updated with every batch, in the batch's
/// transaction, so a deletion resumed after a crash keeps exact counts. Platform-level: the company is gone, so no tenant query filter.
/// </summary>
public class TenantDeletion
{
    private static readonly JsonSerializerOptions CountsJson = new();

    /// <summary>The id the deleted company had. The key: one tombstone per company.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>
    /// HMAC-SHA256 (hex) of the company name, trimmed and upper-cased, with the deployment's privacy key (<c>Privacy:HashKey</c>): "was company
    /// X deleted?" can be answered by whoever holds the key, and the name cannot be recovered or confirmed without it.
    /// </summary>
    public string NameHash { get; private set; } = string.Empty;

    /// <summary>The id of the privacy key <see cref="NameHash"/> was made with (<c>Privacy:HashKeyId</c>), so a rotated key can still check it.</summary>
    public string HashKeyId { get; private set; } = string.Empty;

    public DateTime? CancelledAt { get; private set; }

    public TenantDeletionTrigger Trigger { get; private set; }

    /// <summary>The administrator who asked for it; null when the retention window triggered it.</summary>
    public Guid? RequestedBy { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>
    /// When a run gave up because rows kept appearing (<c>TenantDataDeleter.MaxPasses</c>); the next run retries. Shown to the platform
    /// administrator as a count (monitoring flag <c>tenant_deletion_stuck</c>), never with content.
    /// </summary>
    public DateTime? StuckSince { get; private set; }

    /// <summary>Rows deleted per table, as a JSON object (<c>{"Messages": 1200, ...}</c>).</summary>
    public string Counts { get; private set; } = "{}";

    // Required by EF Core for materialization.
    private TenantDeletion()
    {
    }

    public static TenantDeletion Start(Tenant tenant, string nameHash, string hashKeyId, TenantDeletionTrigger trigger, Guid? requestedBy, DateTime now) => new()
    {
        TenantId = tenant.Id,
        NameHash = KeyedHashes.Required(nameHash),
        HashKeyId = KeyedHashes.RequiredKeyId(hashKeyId),
        CancelledAt = tenant.CancelledAt,
        Trigger = trigger,
        RequestedBy = requestedBy,
        StartedAt = UtcTime.Normalize(now),
    };

    public IReadOnlyDictionary<string, long> DeletedCounts()
        => JsonSerializer.Deserialize<SortedDictionary<string, long>>(Counts, CountsJson) ?? new SortedDictionary<string, long>();

    public long TotalDeleted => DeletedCounts().Values.Sum();

    public void AddDeleted(string table, long rows)
    {
        if (rows < 0)
        {
            throw new DomainException("La cantidad de filas eliminadas no puede ser negativa.");
        }

        if (rows == 0)
        {
            return;
        }

        var counts = new SortedDictionary<string, long>(DeletedCounts().ToDictionary(), StringComparer.Ordinal);
        counts[table] = counts.GetValueOrDefault(table) + rows;
        Counts = JsonSerializer.Serialize(counts, CountsJson);
    }

    public void MarkStuck(DateTime now) => StuckSince ??= UtcTime.Normalize(now);

    public void Complete(DateTime now)
    {
        if (CompletedAt is not null)
        {
            throw new DomainException("La eliminación de esta empresa ya terminó.");
        }

        CompletedAt = UtcTime.Normalize(now);
        StuckSince = null;
    }
}
