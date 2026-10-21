using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(200);

        // Stored by name like every enum (see EnumColumns). The quota is bounded in the database too, so no code path can store nonsense.
        builder.Property(t => t.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(t => t.MaxActiveLines).IsRequired();
        builder.Property(t => t.StatusChangedAt);
        builder.Property(t => t.StatusChangedBy);
        builder.Property(t => t.StatusReason).HasMaxLength(Tenant.StatusReasonMaxLength);
        builder.ToTable(t => t.HasCheckConstraint("CK_Tenants_MaxActiveLines", $"\"MaxActiveLines\" BETWEEN 0 AND {Tenant.MaxLinesLimit}"));

        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt);
    }
}
