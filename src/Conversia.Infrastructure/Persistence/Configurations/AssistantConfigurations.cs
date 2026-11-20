using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>The AI sales assistant's settings (docs/ASSISTANT.md): one row per company, tenant-scoped, deleted with the company's data.</summary>
public class AssistantSettingsConfiguration : IEntityTypeConfiguration<AssistantSettings>
{
    public void Configure(EntityTypeBuilder<AssistantSettings> builder)
    {
        builder.ToTable("AssistantSettings", t =>
        {
            t.HasCheckConstraint("CK_AssistantSettings_ReplyDelay",
                $"\"ReplyDelaySeconds\" BETWEEN {AssistantSettings.MinReplyDelaySeconds} AND {AssistantSettings.MaxReplyDelaySeconds}");
            t.HasCheckConstraint("CK_AssistantSettings_Turns", $"\"MaxAssistantTurns\" BETWEEN {AssistantSettings.MinTurns} AND {AssistantSettings.MaxTurns}");
            t.HasCheckConstraint("CK_AssistantSettings_Cooldown", $"\"HumanCooldownMinutes\" BETWEEN 0 AND {AssistantSettings.MaxCooldownMinutes}");
            // Switched on only with the owner's consent recorded.
            t.HasCheckConstraint("CK_AssistantSettings_Consent", "NOT \"Enabled\" OR (\"TermsVersion\" IS NOT NULL AND \"TermsAcceptedAt\" IS NOT NULL AND \"TermsAcceptedBy\" IS NOT NULL)");
        });
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.TenantId).IsUnique();
        builder.Property(s => s.TermsVersion).HasMaxLength(AssistantSettings.TermsVersionMaxLength);
        builder.Property(s => s.ActiveHours).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(s => s.DisclosureMode).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(s => s.DisclosureText).IsRequired().HasMaxLength(AssistantSettings.LineMaxLength);
        builder.Property(s => s.HandoffLine).IsRequired().HasMaxLength(AssistantSettings.LineMaxLength);
        builder.Property(s => s.HandoffKeywords).IsRequired();
        builder.Property(s => s.ToneNotes).HasMaxLength(AssistantSettings.ToneNotesMaxLength);
        builder.Property(s => s.Language).IsRequired().HasMaxLength(10);
        builder.Ignore(s => s.ReplyDelay);
        builder.Ignore(s => s.HumanCooldown);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// The assistant's state per conversation, and its durable reply timer. The job's claim reads the partial index of armed timers by due time;
/// the row goes before its conversation (RESTRICT: the deletion paths name it).
/// </summary>
public class ConversationAssistantConfiguration : IEntityTypeConfiguration<ConversationAssistant>
{
    public void Configure(EntityTypeBuilder<ConversationAssistant> builder)
    {
        builder.ToTable("ConversationAssistants", t =>
            t.HasCheckConstraint("CK_ConversationAssistants_Timer", "(\"State\" = 'Waiting') = (\"DueAt\" IS NOT NULL)"));
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => a.ConversationId).IsUnique();
        builder.HasIndex(a => a.TenantId);
        builder.HasIndex(a => a.DueAt).HasFilter("\"State\" = 'Waiting'").HasDatabaseName("IX_ConversationAssistants_Due");
        builder.Property(a => a.State).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(a => a.HandoffReason).HasMaxLength(ConversationAssistant.HandoffReasonMaxLength);
        builder.Property(a => a.SendingTextHash).HasMaxLength(ConversationAssistant.TextHashLength);
        builder.Ignore(a => a.IsSendPending);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The versions of a company's knowledge base: business facts, no customer data. At most one published per company.</summary>
public class AssistantKnowledgeVersionConfiguration : IEntityTypeConfiguration<AssistantKnowledgeVersion>
{
    public void Configure(EntityTypeBuilder<AssistantKnowledgeVersion> builder)
    {
        builder.ToTable("AssistantKnowledgeVersions");
        builder.HasKey(v => v.Id);
        builder.HasIndex(v => new { v.TenantId, v.Number }).IsUnique();
        builder.HasIndex(v => v.TenantId).IsUnique().HasFilter("\"Status\" = 'Published'").HasDatabaseName("IX_AssistantKnowledgeVersions_Published");
        builder.Property(v => v.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(v => v.Content).IsRequired().HasColumnType("jsonb");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The append-only history of the assistant's settings and consent.</summary>
public class AssistantAuditEntryConfiguration : IEntityTypeConfiguration<AssistantAuditEntry>
{
    public void Configure(EntityTypeBuilder<AssistantAuditEntry> builder)
    {
        builder.ToTable("AssistantAuditEntries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).IsRequired().HasMaxLength(AssistantAuditEntry.ActionMaxLength);
        builder.Property(e => e.Details).IsRequired().HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.At });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Platform-wide switches (not tenant-scoped).</summary>
public class PlatformSwitchConfiguration : IEntityTypeConfiguration<PlatformSwitch>
{
    public void Configure(EntityTypeBuilder<PlatformSwitch> builder)
    {
        builder.ToTable("PlatformSwitches");
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(64);
    }
}

/// <summary>Counters of the assistant's activity per company, day, sandbox flag and metric (counts only).</summary>
public class AssistantActivityCountConfiguration : IEntityTypeConfiguration<AssistantActivityCount>
{
    public void Configure(EntityTypeBuilder<AssistantActivityCount> builder)
    {
        builder.ToTable("AssistantActivityCounts");
        builder.HasKey(c => new { c.TenantId, c.Day, c.IsSandbox, c.Metric });
        builder.Property(c => c.Metric).HasMaxLength(AssistantActivityCount.MetricMaxLength);
        builder.HasIndex(c => c.Day);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
