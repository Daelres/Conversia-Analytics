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

        builder.Property(l => l.AdminId).IsRequired();
        builder.Property(l => l.Action).IsRequired().HasMaxLength(PlatformAuditLog.ActionMaxLength);
        builder.Property(l => l.TenantId);
        builder.Property(l => l.Details).IsRequired().HasColumnType("jsonb");
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt);

        builder.HasIndex(l => new { l.TenantId, l.CreatedAt });
        builder.HasIndex(l => l.CreatedAt);
    }
}
