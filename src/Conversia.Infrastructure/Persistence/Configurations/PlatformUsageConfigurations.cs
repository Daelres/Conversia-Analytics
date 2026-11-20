using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>
/// The platform's line licence inventory. No TenantId column (the company it is assigned to is <c>AssignedTenantId</c>, data of the platform),
/// so no tenant filter; no foreign key to the company or the line either, like the audit trail: a licence outlives what happens to them.
/// </summary>
public class LineLicenseConfiguration : IEntityTypeConfiguration<LineLicense>
{
    public void Configure(EntityTypeBuilder<LineLicense> builder)
    {
        builder.ToTable("LineLicenses", t =>
        {
            t.HasCheckConstraint("CK_LineLicenses_MonthlyCostUsd", $"\"MonthlyCostUsd\" BETWEEN 0 AND {LineLicense.MaxMonthlyCostUsd:0}");
            t.HasCheckConstraint("CK_LineLicenses_CustomerOwnedCost", "NOT \"CustomerOwned\" OR \"MonthlyCostUsd\" = 0");
            t.HasCheckConstraint("CK_LineLicenses_Assignment", "(\"Status\" = 'Assigned') = (\"AssignedTenantId\" IS NOT NULL)");
        });
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Provider).IsRequired().HasMaxLength(LineLicense.ProviderMaxLength);
        builder.Property(l => l.PhoneDigits).IsRequired().HasMaxLength(LineLicense.PhoneDigitsMaxLength);
        builder.Property(l => l.ProviderChannelId).HasMaxLength(WhatsAppChannel.ProviderPhoneNumberIdMaxLength);
        builder.Property(l => l.MonthlyCostUsd).IsRequired().HasPrecision(10, 2);
        builder.Property(l => l.CustomerOwned).IsRequired();
        builder.Property(l => l.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(l => l.Notes).HasMaxLength(LineLicense.NotesMaxLength);
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.Version).IsRowVersion();
        builder.Ignore(l => l.MaskedPhoneNumber);

        // One live licence per number: retired ones are history.
        builder.HasIndex(l => l.PhoneDigits).IsUnique().HasFilter("\"Status\" <> 'Retired'");
        builder.HasIndex(l => new { l.AssignedTenantId, l.Status });
        builder.HasIndex(l => new { l.Status, l.CreatedAt });
    }
}

/// <summary>AI usage per company and month (design D13). Tenant-scoped like every company table (query filter in the DbContext).</summary>
public class AiUsageMonthConfiguration : IEntityTypeConfiguration<AiUsageMonth>
{
    public void Configure(EntityTypeBuilder<AiUsageMonth> builder)
    {
        builder.ToTable("AiUsageMonths");
        builder.HasKey(u => new { u.TenantId, u.Month, u.IsSandbox });
        builder.Property(u => u.EstimatedUsd).HasPrecision(12, 6);
        builder.Property(u => u.UpdatedAt).IsRequired();

        // The deployment cap sums a month across companies.
        builder.HasIndex(u => u.Month);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>The assistant's model usage per company and month (docs/ASSISTANT.md, "Costs"), tenant-scoped like <see cref="AiUsageMonth"/>.</summary>
public class AssistantUsageMonthConfiguration : IEntityTypeConfiguration<AssistantUsageMonth>
{
    public void Configure(EntityTypeBuilder<AssistantUsageMonth> builder)
    {
        builder.ToTable("AssistantUsageMonths");
        builder.HasKey(u => new { u.TenantId, u.Month, u.IsSandbox });
        builder.Property(u => u.EstimatedUsd).HasPrecision(12, 6);
        builder.Property(u => u.UpdatedAt).IsRequired();

        // The deployment cap sums a month across companies.
        builder.HasIndex(u => u.Month);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>The distinct conversations of a company's month: kept for the current and the previous month only.</summary>
public class AiUsageConversationConfiguration : IEntityTypeConfiguration<AiUsageConversation>
{
    public void Configure(EntityTypeBuilder<AiUsageConversation> builder)
    {
        builder.ToTable("AiUsageConversations");
        builder.HasKey(u => new { u.TenantId, u.Month, u.ConversationId });
        builder.HasIndex(u => u.Month);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>How manual sends ended, per company, day and outcome (platform monitoring). Kept 90 days.</summary>
public class MessageSendOutcomeConfiguration : IEntityTypeConfiguration<MessageSendOutcome>
{
    public void Configure(EntityTypeBuilder<MessageSendOutcome> builder)
    {
        builder.ToTable("MessageSendOutcomes");
        builder.HasKey(o => new { o.TenantId, o.Day, o.IsSandbox, o.Outcome });
        builder.Property(o => o.Outcome).HasMaxLength(MessageSendOutcome.OutcomeMaxLength);
        builder.HasIndex(o => o.Day);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
