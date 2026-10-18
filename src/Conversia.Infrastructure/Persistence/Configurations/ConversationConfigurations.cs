using Conversia.Domain.Entities;
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
        builder.ToTable("Conversations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.ContactId).IsRequired();
        builder.Property(c => c.ChannelId).IsRequired();
        builder.Property(c => c.BranchId).IsRequired();
        builder.Property(c => c.Source).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.SourceReference).HasMaxLength(Conversation.SourceReferenceMaxLength);
        builder.Property(c => c.StartedAt).IsRequired();
        builder.Property(c => c.LastMessageAt).IsRequired();
        builder.Property(c => c.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
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
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt);

        // Idempotency: the provider's id of a message is unique per channel, so a replayed delivery cannot store it twice.
        builder.HasIndex(m => new { m.ChannelId, m.ProviderMessageId }).IsUnique();

        // A conversation's messages in time order.
        builder.HasIndex(m => new { m.ConversationId, m.SentAt });

        builder.HasIndex(m => m.TenantId);

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
        builder.Property(o => o.ServiceInterest).HasMaxLength(Opportunity.ServiceInterestMaxLength);
        builder.Property(o => o.InterestReason).HasMaxLength(Opportunity.ReasonMaxLength);
        builder.Property(o => o.Objection).HasMaxLength(Opportunity.ReasonMaxLength);
        builder.Property(o => o.LossReason).HasMaxLength(Opportunity.ReasonMaxLength);
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt);

        // Phase 1: exactly one opportunity per conversation.
        builder.HasIndex(o => o.ConversationId).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.Stage });

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
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt);

        // The same body delivered twice on a channel is one event: the index arbitrates concurrent deliveries.
        builder.HasIndex(e => new { e.ChannelId, e.BodyHash }).IsUnique();

        // The queue: only pending events are ever searched for the next one to claim.
        builder.HasIndex(e => new { e.NextAttemptAt, e.ReceivedAt })
            .HasFilter("\"Status\" = 'Pending'");

        builder.HasIndex(e => e.TenantId);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(e => e.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}
