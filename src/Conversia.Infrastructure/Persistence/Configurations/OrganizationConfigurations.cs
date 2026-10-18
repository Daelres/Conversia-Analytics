using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.TenantId).IsRequired();

        // citext: "Centro" and "centro" are the same branch name (the unique index below).
        builder.Property(b => b.Name).IsRequired().HasMaxLength(Branch.NameMaxLength).HasColumnType("citext");
        builder.Property(b => b.IsActive).IsRequired();
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.Property(b => b.UpdatedAt);

        builder.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AdvisorConfiguration : IEntityTypeConfiguration<Advisor>
{
    public void Configure(EntityTypeBuilder<Advisor> builder)
    {
        builder.ToTable("Advisors");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.Name).IsRequired().HasMaxLength(Advisor.NameMaxLength);
        builder.Property(a => a.BranchId).IsRequired();
        builder.Property(a => a.IsActive).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt);

        builder.HasIndex(a => a.BranchId);
        builder.HasIndex(a => a.TenantId);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WhatsAppChannelConfiguration : IEntityTypeConfiguration<WhatsAppChannel>
{
    public void Configure(EntityTypeBuilder<WhatsAppChannel> builder)
    {
        builder.ToTable("WhatsAppChannels");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.BranchId).IsRequired();
        builder.Property(c => c.DisplayPhoneNumber).IsRequired().HasMaxLength(WhatsAppChannel.DisplayPhoneNumberMaxLength);
        builder.Property(c => c.ProviderPhoneNumberId).HasMaxLength(WhatsAppChannel.ProviderPhoneNumberIdMaxLength);
        builder.Property(c => c.ChannelKey).IsRequired().HasMaxLength(WhatsAppChannel.ChannelKeyMaxLength);
        builder.Property(c => c.WebhookSecretHash).IsRequired();
        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt);

        // The key sits in a public URL and the webhook resolves the tenant from it, so it is unique across all tenants.
        builder.HasIndex(c => c.ChannelKey).IsUnique();
        builder.HasIndex(c => c.BranchId);
        builder.HasIndex(c => c.TenantId);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(c => c.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}
