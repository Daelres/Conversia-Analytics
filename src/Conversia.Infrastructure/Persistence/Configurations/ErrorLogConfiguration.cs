using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conversia.Infrastructure.Persistence.Configurations;

/// <summary>An operations table: deliberately no tenant filter, and no foreign keys (a row must outlive the user and tenant it names).</summary>
public class ErrorLogConfiguration : IEntityTypeConfiguration<ErrorLog>
{
    public void Configure(EntityTypeBuilder<ErrorLog> builder)
    {
        builder.ToTable("ErrorLogs");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).IsRequired().HasMaxLength(ErrorLog.CodeMaxLength);
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.Source).IsRequired();
        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.Environment).HasMaxLength(ErrorLog.EnvironmentMaxLength);
        builder.Property(e => e.ServiceVersion).HasMaxLength(ErrorLog.ServiceVersionMaxLength);
        builder.Property(e => e.Instance).HasMaxLength(ErrorLog.InstanceMaxLength);
        builder.Property(e => e.HttpMethod).HasMaxLength(ErrorLog.HttpMethodMaxLength);
        builder.Property(e => e.Path).HasMaxLength(ErrorLog.PathMaxLength);
        builder.Property(e => e.Query).HasMaxLength(ErrorLog.QueryMaxLength);
        builder.Property(e => e.Endpoint).HasMaxLength(ErrorLog.EndpointMaxLength);
        builder.Property(e => e.RouteValues).HasMaxLength(ErrorLog.RouteValuesMaxLength);
        builder.Property(e => e.StatusCode).IsRequired();
        builder.Property(e => e.TraceId).HasMaxLength(ErrorLog.TraceIdMaxLength);
        builder.Property(e => e.RequestId).HasMaxLength(ErrorLog.RequestIdMaxLength);
        builder.Property(e => e.UserAgent).HasMaxLength(ErrorLog.UserAgentMaxLength);
        builder.Property(e => e.ExceptionType).IsRequired().HasMaxLength(ErrorLog.ExceptionTypeMaxLength);
        builder.Property(e => e.ExceptionMessage).HasMaxLength(ErrorLog.ExceptionMessageMaxLength);
        builder.Property(e => e.ExceptionDetails);
        builder.Property(e => e.Fingerprint).IsRequired().IsUnicode(false).IsFixedLength().HasMaxLength(ErrorLog.FingerprintLength);
        builder.Property(e => e.Context).HasMaxLength(ErrorLog.ContextMaxLength);

        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt);

        // The unique index is what arbitrates two errors that draw the same code.
        builder.HasIndex(e => e.Code).IsUnique();
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.Fingerprint, e.OccurredAt });
    }
}
