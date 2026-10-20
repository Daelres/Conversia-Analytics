namespace Conversia.Domain.Common;

/// <summary>
/// Base type for every persisted entity in the system. Provides the identity
/// and full audit trail (who created/updated the row and when) that every
/// table in this project must carry.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
