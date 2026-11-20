using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

// Enums are stored by name (varchar): readable in SQL and BI tools, and immune to a reordered enum. Max 20 characters is enough for every value.
internal static class EnumColumns
{
    public const int MaxLength = 20;
}

public class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.WaId).IsRequired().HasMaxLength(Contact.MaxWaIdLength);
        builder.Property(c => c.DisplayName).HasMaxLength(Contact.DisplayNameMaxLength);
        builder.Property(c => c.FirstSeenAt).IsRequired();
        builder.Property(c => c.DisplayNameAt);
        builder.Property(c => c.IsSandbox).IsRequired().HasDefaultValue(false);
         // The profile an LLM extracts (or a person sets), with per-field provenance. Only the coarse range is stored, never an exact age.
        builder.Property(c => c.AgeRange).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.AgeRangeSource).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.Gender).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(Gender.Undetermined);
        builder.Property(c => c.GenderSource).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.ProfileClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.ProfileChangedAt);
        builder.Property(c => c.ProfileChangedBy);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt);

        // One contact per phone per tenant; also what arbitrates two workers creating the same contact.
        builder.HasIndex(c => new { c.TenantId, c.WaId }).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        // A conversation is closed exactly when it has a closing time.
        builder.ToTable("Conversations", t => t.HasCheckConstraint("CK_Conversations_ClosedAt", "(\"Status\" = 'Closed') = (\"ClosedAt\" IS NOT NULL)"));
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.ContactId).IsRequired();
        builder.Property(c => c.ChannelId).IsRequired();
        builder.Property(c => c.BranchId).IsRequired();
        builder.Property(c => c.AdvisorAssignedBy);
        builder.Property(c => c.AdvisorAssignedAt);
        builder.Property(c => c.Source).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.SourceReference).HasMaxLength(Conversation.SourceReferenceMaxLength);
        builder.Property(c => c.ReferralSourceType).HasMaxLength(AdReferral.SourceTypeMaxLength);
        builder.Property(c => c.ReferralSourceId).HasMaxLength(AdReferral.SourceIdMaxLength);
        builder.Property(c => c.ReferralSourceUrl).HasMaxLength(AdReferral.SourceUrlMaxLength);
        builder.Property(c => c.ReferralHeadline).HasMaxLength(AdReferral.HeadlineMaxLength);
        builder.Property(c => c.StartedAt).IsRequired();
        builder.Property(c => c.LastMessageAt).IsRequired();
        builder.Property(c => c.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.ClosedAt);
        builder.Property(c => c.ClassificationDirty).IsRequired();
        builder.Property(c => c.ClassificationRequestedAt);
        builder.Property(c => c.ClassificationFailures).IsRequired();
        builder.Property(c => c.ClassificationAttemptedAt);
        builder.Property(c => c.LlmFailures).IsRequired().HasDefaultValue(0);
        builder.Property(c => c.LastLlmSnapshotHash).HasMaxLength(Conversation.SnapshotHashLength).IsFixedLength();
        builder.Property(c => c.ProfilePending).IsRequired().HasDefaultValue(false);
        builder.Property(c => c.PendingAgeRange).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.PendingGender).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.IsSandbox).IsRequired().HasDefaultValue(false);
        builder.Property(c => c.IsImported).IsRequired().HasDefaultValue(false);
        // The LLM analysis state (docs/CLASSIFICATION.md, "When the AI reads"), apart from the rules' flag.
        builder.Property(c => c.LlmPending).IsRequired().HasDefaultValue(false);
        builder.Property(c => c.LlmLeaseUntil);
        builder.Property(c => c.LlmAttemptedAt);
        builder.Property(c => c.LlmAnalyzedAt);
        builder.Property(c => c.LastInboundAt);
        builder.Property(c => c.LastOutboundAt);
        builder.Property(c => c.CustomerReadMarkedAt);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt);

        // Metrics by branch and by advisor over time.
        builder.HasIndex(c => new { c.BranchId, c.StartedAt });
        builder.HasIndex(c => new { c.AdvisorId, c.StartedAt });

        // At most one open conversation per contact and channel: the rule ingestion relies on, enforced by the database
        // (and the lookup index of "the open conversation of this contact on this channel").
        builder.HasIndex(c => new { c.ContactId, c.ChannelId })
            .IsUnique()
            .HasFilter("\"Status\" = 'Open'");

        builder.HasIndex(c => c.TenantId);

        // The LLM runner claims a company's pending conversations, oldest request first: only pending ones are indexed.
        builder.HasIndex(c => new { c.TenantId, c.ClassificationRequestedAt }, "IX_Conversations_LlmPending")
            .HasFilter("\"LlmPending\"");

        // The conversation list and the metrics read a tenant's conversations by the day they started and by whether they are still open.
        builder.HasIndex(c => new { c.TenantId, c.StartedAt });

        // Conversations closed per day of a company (platform monitoring).
        builder.HasIndex(c => new { c.TenantId, c.ClosedAt });
        builder.HasIndex(c => new { c.TenantId, c.Status, c.LastMessageAt });

        // The close-by-inactivity job looks for the idle open conversations of every tenant at once: only open ones are indexed.
        builder.HasIndex(c => c.LastMessageAt, "IX_Conversations_LastMessageAt_Open")
            .HasFilter("\"Status\" = 'Open'");

        // The classification worker looks for the flagged conversations of every tenant at once: only those are indexed.
        builder.HasIndex(c => c.LastMessageAt, "IX_Conversations_LastMessageAt_ClassificationDirty")
            .HasFilter("\"ClassificationDirty\"");

        // The sandbox rows of a tenant (docs/SANDBOX.md): few or none, so only they are indexed. Serves the clean-up and the sandbox-mode metrics.
        // The inbox (docs/MESSAGING.md, "Inbox"): its change token and its delta read the conversations of a tenant (real or sandbox) by
        // their last change; every conversation has one (set when it is created too).
        builder.HasIndex(c => new { c.TenantId, c.IsSandbox, c.UpdatedAt }, "IX_Conversations_Inbox_Changes");

        // The inbox's "awaiting a reply" part: the open conversations by the customer's last message, oldest first, read with a limit.
        builder.HasIndex(c => new { c.TenantId, c.IsSandbox, c.LastInboundAt }, "IX_Conversations_Inbox_Waiting")
            .HasFilter("\"Status\" = 'Open'");

        // The inbox's other part: the open conversations by their last message, newest first, read with a limit.
        builder.HasIndex(c => new { c.TenantId, c.IsSandbox, c.LastMessageAt }, "IX_Conversations_Inbox_Open")
            .HasFilter("\"Status\" = 'Open'");

        builder.HasIndex(c => c.TenantId, "IX_Conversations_TenantId_Sandbox")
            .HasFilter("\"IsSandbox\"");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(c => c.ContactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(c => c.ChannelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(c => c.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Advisor>().WithMany().HasForeignKey(c => c.AdvisorId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.ConversationId).IsRequired();
        builder.Property(m => m.ChannelId).IsRequired();
        builder.Property(m => m.ProviderMessageId).IsRequired().HasMaxLength(Message.ProviderMessageIdMaxLength);
        builder.Property(m => m.Direction).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(m => m.Type).IsRequired().HasMaxLength(Message.TypeMaxLength);
        builder.Property(m => m.Text).HasMaxLength(Message.TextMaxLength);
        builder.Property(m => m.SentAt).IsRequired();
        builder.Property(m => m.Sender).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(MessageSender.Unknown);
        builder.Property(m => m.SentByUserId);
        builder.Property(m => m.SentByAdvisorId);
        builder.Property(m => m.AssignedAdvisorIdAtReceipt);
        builder.Property(m => m.MediaId).HasMaxLength(MessageMedia.MaxIdLength);
        builder.Property(m => m.MediaMimeType).HasMaxLength(MessageMedia.MaxMimeTypeLength);
        builder.Property(m => m.HasCaption).IsRequired().HasDefaultValue(false);
        builder.Property(m => m.TemplateName).HasMaxLength(MessageTemplate.NameMaxLength);
        builder.Property(m => m.TemplateCategory).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        // The stored copy of the media (docs/MESSAGING.md, "Media"): a random key, never a file name.
        builder.Property(m => m.MediaStatus).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(m => m.MediaObjectKey).HasMaxLength(StoredMediaObject.MaxKeyLength);
        builder.Property(m => m.MediaSizeBytes);
        builder.Property(m => m.MediaContentType).HasMaxLength(MessageMedia.MaxMimeTypeLength);
        builder.Property(m => m.MediaAttempts).IsRequired().HasDefaultValue(0);
        builder.Property(m => m.MediaNextAttemptAt);
        builder.Property(m => m.MediaStoredAt);
        builder.Property(m => m.IsImported).IsRequired().HasDefaultValue(false);
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt);

        // Idempotency: the provider's id of a message is unique per channel, so a replayed delivery cannot store it twice.
        builder.HasIndex(m => new { m.ChannelId, m.ProviderMessageId }).IsUnique();

        // A conversation's messages in time order.
        builder.HasIndex(m => new { m.ConversationId, m.SentAt });

        // The download job's queue (only the pending ones) and the retention job's scan (only the stored ones).
        builder.HasIndex(m => m.MediaNextAttemptAt, "IX_Messages_MediaPending").HasFilter("\"MediaStatus\" = 'Pending'");
        builder.HasIndex(m => new { m.TenantId, m.MediaStoredAt }, "IX_Messages_MediaStored").HasFilter("\"MediaStatus\" = 'Stored'");

        // The message volume of a tenant over a period (and the foreign key to the tenant, which it serves as its first column).
        builder.HasIndex(m => new { m.TenantId, m.SentAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> builder)
    {
        builder.ToTable("Opportunities");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.ConversationId).IsRequired();
        builder.Property(o => o.Stage).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.StageSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.StageChangedAt);
        builder.Property(o => o.StageChangedBy);
        builder.Property(o => o.ServiceInterest).HasMaxLength(Opportunity.ServiceInterestMaxLength);
        builder.Property(o => o.InterestReason).HasMaxLength(Opportunity.InterestReasonMaxLength);
        builder.Property(o => o.Objection).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.LossReason).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.LossDetail).HasMaxLength(Opportunity.DetailMaxLength);
        builder.Property(o => o.ClosedAt);
        builder.Property(o => o.ClassifiedAt);
        builder.Property(o => o.ClassificationConfidence).HasPrecision(3, 2);
        builder.Property(o => o.ClassificationRules).HasMaxLength(Opportunity.ClassificationRulesMaxLength);

        // Per-field provenance (docs/CLASSIFICATION.md): who set each descriptive field, and which classifier wrote it last. Existing rows get
        // Auto sources and NULL producers (unknown, read as Rules: never protected as LLM-sourced).
        builder.Property(o => o.ServiceInterestSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.InterestReasonSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.ObjectionSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.ServiceInterestClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.InterestReasonClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.ObjectionClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.ClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.DetailsChangedAt);
        builder.Property(o => o.ReachedStage).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.ReachedStageSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.ReachedStageClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.MovedOffChannel).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(OffChannelMove.None);
        builder.Property(o => o.OffChannelEvidenceIndex);
        builder.Property(o => o.MovedOffChannelSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.MovedOffChannelClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.ClosedBy).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(ClosedBy.Unknown);
        builder.Property(o => o.ClosedBySource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.ClosedByClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.ClosedOutsideSystem).IsRequired().HasDefaultValue(false);
        builder.Property(o => o.ClosedOutsideSystemSource).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength).HasDefaultValue(FieldSource.Auto);
        builder.Property(o => o.ClosedOutsideSystemClassifiedBy).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(o => o.DetailsChangedBy);
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt);

        // Phase 1: exactly one opportunity per conversation.
        builder.HasIndex(o => o.ConversationId).IsUnique();

        // The funnel by stage, and the outcomes closed in a period.
        builder.HasIndex(o => new { o.TenantId, o.Stage, o.ClosedAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(o => o.ConversationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ChannelId).IsRequired();
        builder.Property(e => e.BodyHash).IsRequired();
        builder.Property(e => e.RawPayload).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.ReceivedAt).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(e => e.Attempts).IsRequired();
        builder.Property(e => e.NextAttemptAt).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(WebhookEvent.LastErrorMaxLength);
        builder.Property(e => e.SkippedMessageCount);
        builder.Property(e => e.StatusCount);
        builder.Property(e => e.StoredMessageCount);
        builder.Property(e => e.DuplicateMessageCount);
        builder.Property(e => e.IsSandbox).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt);

        // The same body delivered twice on a channel is one event: the index arbitrates concurrent deliveries.
        builder.HasIndex(e => new { e.ChannelId, e.BodyHash }).IsUnique();

        // The queue: only pending events are ever searched for the next one to claim.
        builder.HasIndex(e => new { e.NextAttemptAt, e.ReceivedAt })
            .HasFilter("\"Status\" = 'Pending'");

        // The deliveries page of a tenant, newest first (it also serves the foreign key to the tenant as its first column).
        builder.HasIndex(e => new { e.TenantId, e.ReceivedAt });

        // The platform's monitoring counts deliveries of the last 24 hours and 7 days across companies (docs/PLATFORM_ADMIN.md, "Monitoring").
        builder.HasIndex(e => e.ReceivedAt);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(e => e.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// The marks of suppressed contacts (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"): one per company and number hash, looked up by
/// ingestion for every message of a contact it does not know yet.
/// </summary>
public class ContactSuppressionConfiguration : IEntityTypeConfiguration<ContactSuppression>
{
    public void Configure(EntityTypeBuilder<ContactSuppression> builder)
    {
        builder.ToTable("ContactSuppressions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.WaIdHash).IsRequired().HasMaxLength(ContactSuppression.HashLength).IsFixedLength();
        builder.Property(s => s.HashKeyId).IsRequired().HasMaxLength(ContactSuppression.HashKeyIdMaxLength);
        builder.Property(s => s.SuppressedAt).IsRequired();
        builder.Property(s => s.SuppressedBy).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.WaIdHash }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The copies of the channels' templates (docs/MESSAGING.md, "Templates"): one row per name and language of a channel.</summary>
public class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.ToTable("MessageTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.ChannelId).IsRequired();
        builder.Property(t => t.Name).IsRequired().HasMaxLength(MessageTemplate.NameMaxLength);
        builder.Property(t => t.Language).IsRequired().HasMaxLength(MessageTemplate.LanguageMaxLength);
        builder.Property(t => t.Category).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(t => t.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(t => t.BodyText).HasMaxLength(MessageTemplate.BodyMaxLength);
        builder.Property(t => t.ParameterNames).IsRequired();
        builder.Property(t => t.IsSupported).IsRequired();
        builder.Property(t => t.SyncedAt).IsRequired();
        builder.Property(t => t.StatusChangedAt);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt);

        builder.Ignore(t => t.ParameterCount);
        builder.Ignore(t => t.NamedParameters);
        builder.Ignore(t => t.CanBeSent);

        builder.HasIndex(t => new { t.ChannelId, t.Name, t.Language }).IsUnique();
        builder.HasIndex(t => t.TenantId);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(t => t.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MediaObjectDeletionConfiguration : IEntityTypeConfiguration<MediaObjectDeletion>
{
    public void Configure(EntityTypeBuilder<MediaObjectDeletion> builder)
    {
        builder.ToTable("MediaObjectDeletions");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.ObjectKey).IsRequired().HasMaxLength(StoredMediaObject.MaxKeyLength);
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt);
        builder.Property(d => d.Attempts).IsRequired().HasDefaultValue(0);
        builder.Property(d => d.NextAttemptAt);
        builder.HasIndex(d => new { d.TenantId, d.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
