using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

// The billing tables (docs/BILLING.md, "Data model"). Plans, add-ons and events are platform-level; the rest is tenant-scoped (query filter in
// ApplicationDbContext) and deleted with the company's data (TenantDataDeletionPlan). Foreign keys are RESTRICT like everywhere else.

public class BillingPlanConfiguration : IEntityTypeConfiguration<BillingPlan>
{
    public void Configure(EntityTypeBuilder<BillingPlan> builder)
    {
        builder.ToTable("BillingPlans", t =>
        {
            t.HasCheckConstraint("CK_BillingPlans_Price", $"\"PricePerLineInCents\" BETWEEN 0 AND {BillingMoney.MaxPriceInCents}");
            t.HasCheckConstraint("CK_BillingPlans_IncludedAi", $"\"IncludedAiConversations\" BETWEEN 0 AND {Tenant.MaxAiMonthlyConversationQuota}");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).IsRequired().HasMaxLength(BillingPlan.CodeMaxLength);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(BillingPlan.NameMaxLength);
        builder.Property(p => p.PricePerLineInCents).IsRequired();
        builder.Property(p => p.IncludedAiConversations).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();
        builder.HasIndex(p => p.Code).IsUnique();
    }
}

public class BillingAddOnConfiguration : IEntityTypeConfiguration<BillingAddOn>
{
    public void Configure(EntityTypeBuilder<BillingAddOn> builder)
    {
        builder.ToTable("BillingAddOns", t =>
        {
            t.HasCheckConstraint("CK_BillingAddOns_Price", $"\"PriceInCents\" BETWEEN 0 AND {BillingMoney.MaxPriceInCents}");
            t.HasCheckConstraint("CK_BillingAddOns_Conversations", $"\"Conversations\" BETWEEN 1 AND {Tenant.MaxAiMonthlyConversationQuota}");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Code).IsRequired().HasMaxLength(BillingPlan.CodeMaxLength);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(BillingPlan.NameMaxLength);
        builder.HasOne<BillingPlan>().WithMany().HasForeignKey(a => a.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.PlanId, a.Code }).IsUnique();
    }
}

public class BillingSubscriptionConfiguration : IEntityTypeConfiguration<BillingSubscription>
{
    public void Configure(EntityTypeBuilder<BillingSubscription> builder)
    {
        builder.ToTable("BillingSubscriptions", t =>
        {
            t.HasCheckConstraint("CK_BillingSubscriptions_AnchorDay", "\"AnchorDay\" BETWEEN 1 AND 31");
            t.HasCheckConstraint("CK_BillingSubscriptions_Period", "\"CurrentPeriodEnd\" >= \"CurrentPeriodStart\"");
            t.HasCheckConstraint("CK_BillingSubscriptions_AddOn", $"(\"AddOnId\" IS NULL AND \"AddOnQuantity\" = 0) OR (\"AddOnId\" IS NOT NULL AND \"AddOnQuantity\" BETWEEN 1 AND {BillingAddOn.MaxQuantity})");
            t.HasCheckConstraint("CK_BillingSubscriptions_BilledLines", $"\"BilledLinesOverride\" IS NULL OR \"BilledLinesOverride\" BETWEEN 1 AND {Tenant.MaxLinesLimit}");
            t.HasCheckConstraint("CK_BillingSubscriptions_PaymentMethod", "(\"PaymentSourceId\" IS NULL) = (\"PaymentMethodType\" IS NULL)");
        });
        builder.HasKey(s => s.Id);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BillingPlan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BillingAddOn>().WithMany().HasForeignKey(s => s.AddOnId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(s => s.CancellationReasonCode).HasMaxLength(ReasonCodes.MaxLength);
        builder.Property(s => s.PaymentSourceId).HasMaxLength(BillingSubscription.PaymentSourceIdMaxLength);
        builder.Property(s => s.PaymentMethodType).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(s => s.PaymentMethodLabel).HasMaxLength(BillingSubscription.PaymentMethodLabelMaxLength);
        builder.Property(s => s.BillingEmail).HasMaxLength(BillingSubscription.EmailMaxLength);
        builder.Property(s => s.Version).IsRowVersion();

        // One live subscription per company (the arbiter of two administrators starting it at once); cancelled ones stay as history, so a
        // company reactivated after a cancellation can be billed again.
        builder.HasIndex(s => s.TenantId).IsUnique().HasFilter("\"Status\" <> 'Cancelled'").HasDatabaseName("IX_BillingSubscriptions_OneLivePerTenant");
        builder.HasIndex(s => new { s.TenantId, s.CreatedAt });
        // The job's queue: what is due, most overdue first (cancelled ones have no NextRunAt).
        builder.HasIndex(s => s.NextRunAt).HasFilter("\"NextRunAt\" IS NOT NULL");
    }
}

public class BillingChargeConfiguration : IEntityTypeConfiguration<BillingCharge>
{
    public void Configure(EntityTypeBuilder<BillingCharge> builder)
    {
        builder.ToTable("BillingCharges", t =>
        {
            t.HasCheckConstraint("CK_BillingCharges_Amount", "\"AmountInCents\" >= 0");
            t.HasCheckConstraint("CK_BillingCharges_Period", "\"PeriodEnd\" > \"PeriodStart\"");
            t.HasCheckConstraint("CK_BillingCharges_Currency", $"\"Currency\" = '{BillingMoney.Currency}'");
            t.HasCheckConstraint("CK_BillingCharges_Paid", "(\"Status\" = 'Paid') = (\"PaidAt\" IS NOT NULL)");
        });
        builder.HasKey(c => c.Id);
        builder.HasOne<BillingSubscription>().WithMany().HasForeignKey(c => c.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.PlanCode).IsRequired().HasMaxLength(BillingPlan.CodeMaxLength);
        builder.Property(c => c.PlanName).IsRequired().HasMaxLength(BillingPlan.NameMaxLength);
        builder.Property(c => c.AddOnName).HasMaxLength(BillingPlan.NameMaxLength);
        builder.Property(c => c.Currency).IsRequired().HasMaxLength(3).IsFixedLength();
        builder.Property(c => c.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.Settlement).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(c => c.Note).HasMaxLength(BillingCharge.NoteMaxLength);
        builder.Property(c => c.PaymentLinkId).HasMaxLength(BillingCharge.PaymentLinkIdMaxLength);
        builder.Property(c => c.PaymentLinkUrl).HasMaxLength(BillingCharge.PaymentLinkUrlMaxLength);
        builder.Property(c => c.Version).IsRowVersion();

        // A period is charged once.
        builder.HasIndex(c => new { c.SubscriptionId, c.PeriodStart }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.PeriodStart });
        builder.HasIndex(c => c.PaymentLinkId).IsUnique().HasFilter("\"PaymentLinkId\" IS NOT NULL");
        // At most one open charge per subscription: the engine never rolls a period while one is open.
        builder.HasIndex(c => c.SubscriptionId).IsUnique().HasFilter("\"Status\" = 'Open'").HasDatabaseName("IX_BillingCharges_OneOpenPerSubscription");
    }
}

public class BillingPaymentAttemptConfiguration : IEntityTypeConfiguration<BillingPaymentAttempt>
{
    public void Configure(EntityTypeBuilder<BillingPaymentAttempt> builder)
    {
        builder.ToTable("BillingPaymentAttempts", t => t.HasCheckConstraint("CK_BillingPaymentAttempts_Amount", "\"AmountInCents\" >= 0"));
        builder.HasKey(a => a.Id);
        builder.HasOne<BillingCharge>().WithMany().HasForeignKey(a => a.ChargeId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.Reference).IsRequired().HasMaxLength(BillingPaymentAttempt.ReferenceMaxLength);
        builder.Property(a => a.GatewayTransactionId).HasMaxLength(BillingPaymentAttempt.TransactionIdMaxLength);
        builder.Property(a => a.Kind).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(a => a.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(a => a.Currency).IsRequired().HasMaxLength(3).IsFixedLength();
        builder.Property(a => a.MethodType).HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(a => a.StatusReason).HasMaxLength(BillingPaymentAttempt.StatusReasonMaxLength);
        builder.Property(a => a.ReviewReason).HasMaxLength(BillingPaymentAttempt.ReviewReasonMaxLength);
        builder.HasIndex(a => a.ReviewFlaggedAt).HasFilter("\"ReviewReason\" IS NOT NULL AND \"ReviewedAt\" IS NULL");

        // Idempotent by our reference and by the gateway's transaction.
        builder.HasIndex(a => a.Reference).IsUnique();
        builder.HasIndex(a => a.GatewayTransactionId).IsUnique().HasFilter("\"GatewayTransactionId\" IS NOT NULL");
        builder.HasIndex(a => a.ChargeId);
        // At most one pending attempt we started per charge: no double charge, whatever runs at once. (A payment through the link is the
        // payer's own, reported by the gateway, and is never blocked.)
        builder.HasIndex(a => a.ChargeId).IsUnique().HasFilter("\"Status\" = 'Pending' AND \"Kind\" <> 'PaymentLink'").HasDatabaseName("IX_BillingPaymentAttempts_OnePendingPerCharge");
    }
}

public class BillingEventConfiguration : IEntityTypeConfiguration<BillingEvent>
{
    public void Configure(EntityTypeBuilder<BillingEvent> builder)
    {
        builder.ToTable("BillingEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.BodyHash).IsRequired().HasMaxLength(Domain.Entities.WebhookEvent.BodyHashLength);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(BillingEvent.EventTypeMaxLength);
        builder.Property(e => e.TransactionId).HasMaxLength(BillingPaymentAttempt.TransactionIdMaxLength);
        builder.Property(e => e.PaymentLinkId).HasMaxLength(BillingCharge.PaymentLinkIdMaxLength);
        builder.Property(e => e.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(e => e.LastError).HasMaxLength(BillingEvent.LastErrorMaxLength);
        builder.HasIndex(e => e.BodyHash).IsUnique();
        builder.HasIndex(e => e.NextAttemptAt).HasFilter("\"Status\" = 'Pending'");
        builder.HasIndex(e => e.ReceivedAt);
    }
}

public class AiQuotaGrantConfiguration : IEntityTypeConfiguration<AiQuotaGrant>
{
    public void Configure(EntityTypeBuilder<AiQuotaGrant> builder)
    {
        builder.ToTable("AiQuotaGrants", t =>
        {
            t.HasCheckConstraint("CK_AiQuotaGrants_Conversations", "\"Conversations\" > 0");
            t.HasCheckConstraint("CK_AiQuotaGrants_Window", "\"ValidUntil\" > \"ValidFrom\"");
        });
        builder.HasKey(g => g.Id);
        builder.HasOne<BillingCharge>().WithMany().HasForeignKey(g => g.ChargeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(g => g.ChargeId).IsUnique();
        builder.HasIndex(g => new { g.TenantId, g.ValidUntil });
    }
}

/// <summary>Durable review items (docs/BILLING.md, "Review flags"): platform-level, the company is data (no foreign key: it outlives a deletion).</summary>
public class BillingReviewItemConfiguration : IEntityTypeConfiguration<BillingReviewItem>
{
    public void Configure(EntityTypeBuilder<BillingReviewItem> builder)
    {
        builder.ToTable("BillingReviewItems");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Reason).IsRequired().HasMaxLength(BillingPaymentAttempt.ReviewReasonMaxLength);
        builder.Property(r => r.TransactionId).HasMaxLength(BillingPaymentAttempt.TransactionIdMaxLength);
        builder.Property(r => r.Currency).HasMaxLength(3);
        builder.Property(r => r.ReviewNote).HasMaxLength(BillingCharge.NoteMaxLength);
        builder.HasIndex(r => r.FlaggedAt).HasFilter("\"ReviewedAt\" IS NULL");
        builder.HasIndex(r => new { r.TenantId, r.FlaggedAt });
    }
}
