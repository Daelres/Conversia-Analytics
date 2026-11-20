using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.TenantId)
            .IsRequired();

        // citext: case-insensitive text, so "Ana@x.com" and "ana@x.com" are the same address for lookups and for the unique index.
        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(256)
            .HasColumnType("citext");

        // Email is globally unique (ignoring case) — see docs/USERS_TENANTS_PERMISSIONS.md,
        // "Login and tenant resolution".
        builder.HasIndex(u => u.Email)
            .IsUnique();

        builder.Property(u => u.PasswordHash)
            .IsRequired();

        builder.Property(u => u.IsOwner)
            .IsRequired()
            .HasDefaultValue(false);

        // The WhatsApp opt-in of the manager reports (docs/REPORTS.md): the number exists only together with the consent.
        builder.Property(u => u.ReportsWhatsAppNumber).HasMaxLength(LineLicense.PhoneDigitsMaxLength);
        builder.Ignore(u => u.ReceivesReportsOnWhatsApp);
        builder.Property(u => u.ReportsWhatsAppCodeHash).HasMaxLength(64);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Users_ReportsWhatsAppOptIn", "(\"ReportsWhatsAppNumber\" IS NULL) = (\"ReportsWhatsAppOptInAt\" IS NULL)");
            t.HasCheckConstraint("CK_Users_ReportsWhatsAppVerified", "\"ReportsWhatsAppVerifiedAt\" IS NULL OR \"ReportsWhatsAppNumber\" IS NOT NULL");
        });

        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
