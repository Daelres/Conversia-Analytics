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
        builder.Property(t => t.SandboxMode).IsRequired().HasDefaultValue(false);
        builder.ToTable(t => t.HasCheckConstraint("CK_Tenants_MaxActiveLines", $"\"MaxActiveLines\" BETWEEN 0 AND {Tenant.MaxLinesLimit}"));

        // The per-company limits (docs/PLATFORM_ADMIN.md, "Limits"): AI off by default; null quota and rate = the deployment defaults.
        builder.Property(t => t.AiEnabled).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.AiTermsVersion).HasMaxLength(Tenant.AiTermsVersionMaxLength);
        builder.Property(t => t.AiEnabledChangedAt);
        builder.Property(t => t.AiMonthlyConversationQuota);
        builder.Property(t => t.SendRateLimitPerMinute);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_AiMonthlyConversationQuota", $"\"AiMonthlyConversationQuota\" IS NULL OR \"AiMonthlyConversationQuota\" BETWEEN 0 AND {Tenant.MaxAiMonthlyConversationQuota}"));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_SendRateLimitPerMinute", $"\"SendRateLimitPerMinute\" IS NULL OR \"SendRateLimitPerMinute\" BETWEEN 1 AND {Tenant.MaxSendRateLimitPerMinute}"));

        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt);
    }
}
