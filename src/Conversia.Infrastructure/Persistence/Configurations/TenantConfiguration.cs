using Conversia.Domain.Common;
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
        builder.Property(t => t.ReadReceiptsEnabled).IsRequired().HasDefaultValue(false);
        builder.ToTable(t => t.HasCheckConstraint("CK_Tenants_MaxActiveLines", $"\"MaxActiveLines\" BETWEEN 0 AND {Tenant.MaxLinesLimit}"));

        // The per-company limits (docs/PLATFORM_ADMIN.md, "Limits"): AI off by default; null quota and rate = the deployment defaults.
        builder.Property(t => t.AiEnabled).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.AiTermsVersion).HasMaxLength(Tenant.AiTermsVersionMaxLength);
        builder.Property(t => t.AiEnabledChangedAt);
        builder.Property(t => t.AiMonthlyConversationQuota);
        builder.Property(t => t.SendRateLimitPerMinute);
        builder.Property(t => t.MediaRetentionDays);
        // When the LLM reads the company (docs/CLASSIFICATION.md, "When the AI reads"): nightly by default.
        builder.Property(t => t.AiAnalysisMode).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength)
            .HasDefaultValue(Domain.Enums.AiAnalysisMode.Nightly).HasSentinel((Domain.Enums.AiAnalysisMode)(-1));
        builder.Property(t => t.AiAnalysisNightlyHour);
        builder.Property(t => t.AiAnalysisIntervalHours);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_AiAnalysisSchedule",
            "(\"AiAnalysisMode\" = 'Nightly' AND \"AiAnalysisIntervalHours\" IS NULL AND (\"AiAnalysisNightlyHour\" IS NULL OR \"AiAnalysisNightlyHour\" BETWEEN 0 AND 23))"
            + $" OR (\"AiAnalysisMode\" = 'EveryHours' AND \"AiAnalysisNightlyHour\" IS NULL AND \"AiAnalysisIntervalHours\" BETWEEN 1 AND {Tenant.MaxAiAnalysisIntervalHours})"
            + " OR (\"AiAnalysisMode\" = 'NearRealTime' AND \"AiAnalysisNightlyHour\" IS NULL AND \"AiAnalysisIntervalHours\" IS NULL)"));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_AiMonthlyConversationQuota", $"\"AiMonthlyConversationQuota\" IS NULL OR \"AiMonthlyConversationQuota\" BETWEEN 0 AND {Tenant.MaxAiMonthlyConversationQuota}"));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_SendRateLimitPerMinute", $"\"SendRateLimitPerMinute\" IS NULL OR \"SendRateLimitPerMinute\" BETWEEN 1 AND {Tenant.MaxSendRateLimitPerMinute}"));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_MediaRetentionDays", $"\"MediaRetentionDays\" IS NULL OR \"MediaRetentionDays\" BETWEEN 1 AND {Tenant.MaxMediaRetentionDays}"));

        // The lifecycle (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): a suspension always says why, and only a cancelled company has a
        // cancellation and a deletion date. Bounded in the database too, so the workers' SQL can trust the columns.
        builder.Property(t => t.StatusReasonCode).HasMaxLength(ReasonCodes.MaxLength);
        builder.Property(t => t.SuspensionCause).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(t => t.CancelledAt);
        builder.Property(t => t.DataDeletionDueAt);
        builder.Property(t => t.DataDeletionStartedAt);
        builder.Property(t => t.BillingHoldSince);
        builder.ToTable(t => t.HasCheckConstraint("CK_Tenants_SuspensionCause", "(\"Status\" = 'Suspended') = (\"SuspensionCause\" IS NOT NULL)"));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_Cancellation",
            "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL AND \"DataDeletionDueAt\" IS NOT NULL) AND (\"DataDeletionStartedAt\" IS NULL OR \"Status\" = 'Cancelled')"));

        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt);
    }
}
