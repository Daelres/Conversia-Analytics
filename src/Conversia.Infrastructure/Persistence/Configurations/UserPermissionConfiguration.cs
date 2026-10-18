using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

public class UserPermissionConfiguration : IEntityTypeConfiguration<UserPermission>
{
    public void Configure(EntityTypeBuilder<UserPermission> builder)
    {
        builder.ToTable("UserPermissions");

        builder.HasKey(up => up.Id);

        builder.Property(up => up.UserId)
            .IsRequired();

        builder.Property(up => up.Permission)
            .IsRequired()
            .HasConversion<int>();

        // A user cannot hold the same permission twice —
        // docs/USERS_TENANTS_PERMISSIONS.md.
        builder.HasIndex(up => new { up.UserId, up.Permission })
            .IsUnique();

        builder.Property(up => up.CreatedAt).IsRequired();
        builder.Property(up => up.UpdatedAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(up => up.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
