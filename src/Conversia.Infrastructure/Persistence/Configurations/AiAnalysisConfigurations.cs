using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>The LLM runner's counts per company and window (tenant-scoped; counts only).</summary>
public class AiAnalysisRunConfiguration : IEntityTypeConfiguration<AiAnalysisRun>
{
    public void Configure(EntityTypeBuilder<AiAnalysisRun> builder)
    {
        builder.ToTable("AiAnalysisRuns");
        builder.HasKey(r => new { r.TenantId, r.WindowStart });
        builder.Property(r => r.Mode).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.HasIndex(r => r.WindowStart);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Message Batches sent to the provider: a platform table, no text.</summary>
public class LlmBatchConfiguration : IEntityTypeConfiguration<LlmBatch>
{
    public void Configure(EntityTypeBuilder<LlmBatch> builder)
    {
        builder.ToTable("LlmBatches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.ProviderBatchId).HasMaxLength(LlmBatch.ProviderBatchIdMaxLength);
        builder.Property(b => b.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(b => b.Error).HasMaxLength(LlmBatch.ErrorMaxLength);
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.HasIndex(b => new { b.Status, b.CreatedAt });
    }
}

/// <summary>The conversations of a batch (tenant-scoped). Deleted with their conversation or company: they only steer the collection.</summary>
public class LlmBatchRequestConfiguration : IEntityTypeConfiguration<LlmBatchRequest>
{
    public void Configure(EntityTypeBuilder<LlmBatchRequest> builder)
    {
        builder.ToTable("LlmBatchRequests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.CustomId).IsRequired().HasMaxLength(LlmBatchRequest.CustomIdMaxLength);
        builder.Property(r => r.SnapshotHash).IsRequired().HasMaxLength(Conversation.SnapshotHashLength).IsFixedLength();
        builder.Property(r => r.StageAtPlan).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(r => r.Status).IsRequired().HasConversion<string>().HasMaxLength(EnumColumns.MaxLength);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.EstimatedUsd).HasPrecision(12, 6);
        builder.HasIndex(r => new { r.BatchId, r.CustomId }).IsUnique();
        // Admission reserves the unresolved requests of a company and of the deployment.
        builder.HasIndex(r => new { r.Status, r.TenantId });
        builder.HasIndex(r => new { r.ConversationId, r.CreatedAt });
        builder.HasIndex(r => r.TenantId);
        builder.HasOne<LlmBatch>().WithMany().HasForeignKey(r => r.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(r => r.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
