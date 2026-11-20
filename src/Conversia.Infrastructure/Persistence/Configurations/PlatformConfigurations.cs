using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>Platform-level table: no TenantId, no query filter. Only the admin login and the admin API read it.</summary>
public class PlatformAdminConfiguration : IEntityTypeConfiguration<PlatformAdmin>
{
    public void Configure(EntityTypeBuilder<PlatformAdmin> builder)
    {
        builder.ToTable("PlatformAdmins");
        builder.HasKey(a => a.Id);

        // citext like Users.Email: unique ignoring case, and the arbiter of two bootstraps racing.
        builder.Property(a => a.Email).IsRequired().HasMaxLength(PlatformAdmin.EmailMaxLength).HasColumnType("citext");
        builder.Property(a => a.PasswordHash).IsRequired();
        builder.Property(a => a.IsActive).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt);

        builder.HasIndex(a => a.Email).IsUnique();
    }
}

/// <summary>Append-only trail of what administrators changed. TenantId is a plain column (no FK) so the trail never blocks, nor is lost with, a company.</summary>
public class PlatformAuditLogConfiguration : IEntityTypeConfiguration<PlatformAuditLog>
{
    public void Configure(EntityTypeBuilder<PlatformAuditLog> builder)
    {
        builder.ToTable("PlatformAuditLogs");
        builder.HasKey(l => l.Id);

        // No administrator: the system made the change (a background job), and then the reason code says why.
        builder.Property(l => l.AdminId);
        builder.Property(l => l.ReasonCode).HasMaxLength(ReasonCodes.MaxLength);
        builder.ToTable(t => t.HasCheckConstraint("CK_PlatformAuditLogs_SystemReasonCode", "\"AdminId\" IS NOT NULL OR \"ReasonCode\" IS NOT NULL"));
        builder.Property(l => l.Action).IsRequired().HasMaxLength(PlatformAuditLog.ActionMaxLength);
        builder.Property(l => l.TenantId);
        builder.Property(l => l.Details).IsRequired().HasColumnType("jsonb");
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt);

        builder.HasIndex(l => new { l.TenantId, l.CreatedAt });
        builder.HasIndex(l => l.CreatedAt);
    }
}

/// <summary>
/// The tombstone of a deleted company (docs/PLATFORM_ADMIN.md, "Deleting a company's data"). Keyed by the id the company had; no foreign key,
/// since the company row is gone. Platform-level like the audit trail.
/// </summary>
public class TenantDeletionConfiguration : IEntityTypeConfiguration<TenantDeletion>
{
    public void Configure(EntityTypeBuilder<TenantDeletion> builder)
    {
        builder.ToTable("TenantDeletions");
        builder.HasKey(d => d.TenantId);
        builder.Property(d => d.TenantId).ValueGeneratedNever();
        builder.Property(d => d.NameHash).IsRequired().HasMaxLength(KeyedHashes.Length).IsFixedLength();
        builder.Property(d => d.HashKeyId).IsRequired().HasMaxLength(KeyedHashes.KeyIdMaxLength);
        builder.Property(d => d.StuckSince);
        builder.Property(d => d.CancelledAt);
        builder.Property(d => d.Trigger).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(d => d.RequestedBy);
        builder.Property(d => d.StartedAt).IsRequired();
        builder.Property(d => d.CompletedAt);
        builder.Property(d => d.Counts).IsRequired().HasColumnType("jsonb");
        builder.HasIndex(d => d.CompletedAt);
    }
}
