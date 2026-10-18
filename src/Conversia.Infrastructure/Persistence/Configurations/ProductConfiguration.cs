using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(Product.NameMaxLength);

        // citext: case-insensitive text, so the unique (TenantId, Sku) index below treats "abc" and "ABC" as the same SKU.
        builder.Property(p => p.Sku)
            .IsRequired()
            .HasMaxLength(Product.SkuMaxLength)
            .HasColumnType("citext");

        builder.Property(p => p.Price)
            .HasColumnType("numeric(18,2)");

        builder.Property(p => p.Stock)
            .IsRequired();

        // Npgsql maps a uint row version to the xmin system column: PostgreSQL rewrites it on every UPDATE, and EF Core
        // adds it to the WHERE clause of every UPDATE/DELETE of a tracked product (a stale write affects 0 rows).
        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt);

        // SKUs are unique per tenant, ignoring case (the citext column); the index is also what arbitrates concurrent creates.
        builder.HasIndex(p => new { p.TenantId, p.Sku })
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
