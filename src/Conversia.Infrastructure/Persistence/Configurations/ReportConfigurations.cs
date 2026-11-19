using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>A company's manager report definitions (docs/REPORTS.md). Tenant-scoped; deleted with the company's data.</summary>
public class ReportDefinitionConfiguration : IEntityTypeConfiguration<ReportDefinition>
{
    public void Configure(EntityTypeBuilder<ReportDefinition> builder)
    {
        builder.ToTable("ReportDefinitions", t =>
        {
            t.HasCheckConstraint("CK_ReportDefinitions_SendHour", "\"SendHour\" BETWEEN 0 AND 23");
            t.HasCheckConstraint("CK_ReportDefinitions_SendDayOfWeek", "\"SendDayOfWeek\" BETWEEN 1 AND 7");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(ReportDefinition.NameMaxLength);
        builder.Property(d => d.Frequency).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(d => d.Sections).IsRequired().HasConversion<int>();
        builder.Property(d => d.BranchIds).IsRequired();
        builder.Property(d => d.WhatsAppTemplateName).HasMaxLength(MessageTemplate.NameMaxLength);
        builder.Property(d => d.WhatsAppTemplateLanguage).HasMaxLength(MessageTemplate.LanguageMaxLength);
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.HasMany(d => d.Recipients).WithOne().HasForeignKey(r => r.DefinitionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Recipients).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The job reads the active definitions of every company.
        builder.HasIndex(d => new { d.IsActive, d.TenantId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReportRecipientConfiguration : IEntityTypeConfiguration<ReportRecipient>
{
    public void Configure(EntityTypeBuilder<ReportRecipient> builder)
    {
        builder.ToTable("ReportRecipients");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Channels).IsRequired().HasConversion<int>();
        builder.Ignore(r => r.ReceivesEmail);
        builder.Ignore(r => r.ReceivesWhatsApp);
        builder.HasIndex(r => new { r.DefinitionId, r.UserId }).IsUnique();
        builder.HasIndex(r => r.TenantId);
        // A user who leaves the company stops receiving its reports: the row goes with the user.
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Generated reports: aggregates and text, one per definition and period (the idempotency key).</summary>
public class ManagerReportConfiguration : IEntityTypeConfiguration<ManagerReport>
{
    public void Configure(EntityTypeBuilder<ManagerReport> builder)
    {
        builder.ToTable("ManagerReports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.DefinitionName).IsRequired().HasMaxLength(ReportDefinition.NameMaxLength);
        builder.Property(r => r.Frequency).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(r => r.Generator).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(r => r.FallbackReason).HasMaxLength(ManagerReport.FallbackReasonMaxLength);
        builder.Property(r => r.Model).HasMaxLength(ManagerReport.ModelMaxLength);
        builder.Property(r => r.EstimatedUsd).HasPrecision(12, 6);
        builder.Property(r => r.DataPackVersion).IsRequired().HasMaxLength(ManagerReport.DataPackVersionMaxLength);
        builder.Property(r => r.Content).IsRequired().HasColumnType("jsonb");
        builder.Property(r => r.DataPack).IsRequired().HasColumnType("jsonb");
        builder.Property(r => r.Html).IsRequired();
        builder.Property(r => r.Text).IsRequired();
        builder.Property(r => r.Headline).IsRequired().HasMaxLength(ManagerReport.HeadlineMaxLength);
        builder.Property(r => r.GeneratedAt).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();

        // One report per definition, kind of period and period: a change of frequency never makes a period look already done.
        builder.HasIndex(r => new { r.DefinitionId, r.Frequency, r.PeriodFrom }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.GeneratedAt });
        // Retention and the monitoring read by generation time across companies.
        builder.HasIndex(r => r.GeneratedAt);
        builder.HasOne<ReportDefinition>().WithMany().HasForeignKey(r => r.DefinitionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReportDeliveryConfiguration : IEntityTypeConfiguration<ReportDelivery>
{
    public void Configure(EntityTypeBuilder<ReportDelivery> builder)
    {
        builder.ToTable("ReportDeliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Channel).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(d => d.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(d => d.ErrorCode).HasMaxLength(ReportDelivery.ErrorCodeMaxLength);
        builder.Property(d => d.ProviderMessageId).HasMaxLength(ReportDelivery.ProviderMessageIdMaxLength);
        builder.Property(d => d.CreatedAt).IsRequired();

        // No duplicate sends: one delivery per report, person and channel.
        builder.HasIndex(d => new { d.ReportId, d.UserId, d.Channel }).IsUnique();
        // The sender claims the due pending ones.
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt });
        builder.HasIndex(d => d.TenantId);
        builder.HasOne<ManagerReport>().WithMany().HasForeignKey(d => d.ReportId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>The ledger of written reports (cost and model use, previews included). No foreign key to the definition: the spend outlives it.</summary>
public class ReportLedgerEntryConfiguration : IEntityTypeConfiguration<ReportLedgerEntry>
{
    public void Configure(EntityTypeBuilder<ReportLedgerEntry> builder)
    {
        builder.ToTable("ReportLedgerEntries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(e => e.Frequency).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(e => e.Generator).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(e => e.FallbackReason).HasMaxLength(ManagerReport.FallbackReasonMaxLength);
        builder.Property(e => e.EstimatedUsd).HasPrecision(12, 6);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
        builder.HasIndex(e => e.CreatedAt); // the deployment's monthly spend
        builder.HasIndex(e => new { e.DefinitionId, e.Frequency, e.PeriodFrom });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
