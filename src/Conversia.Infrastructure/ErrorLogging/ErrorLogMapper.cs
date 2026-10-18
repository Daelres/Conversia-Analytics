using Conversia.Application.DTOs.Diagnostics;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;

namespace Conversia.Infrastructure.ErrorLogging;

internal static class ErrorLogMapper
{
    /// <summary>The row for an already sanitized report; <paramref name="code"/> is passed apart because a collision retries with another.</summary>
    public static ErrorLog ToEntity(ErrorReport report, string code) => new()
    {
        Code = code,
        OccurredAt = DatabaseTime.Truncate(report.OccurredAt),
        Source = report.Source,
        Kind = report.Kind,
        Environment = report.Environment,
        ServiceVersion = report.ServiceVersion,
        Instance = report.Instance,
        HttpMethod = report.HttpMethod,
        Path = report.Path,
        Query = report.Query,
        Endpoint = report.Endpoint,
        RouteValues = report.RouteValues,
        StatusCode = report.StatusCode,
        TraceId = report.TraceId,
        RequestId = report.RequestId,
        UserId = report.UserId,
        TenantId = report.TenantId,
        UserAgent = report.UserAgent,
        ExceptionType = report.ExceptionType,
        ExceptionMessage = report.ExceptionMessage,
        ExceptionDetails = report.ExceptionDetails,
        Fingerprint = report.Fingerprint ?? string.Empty,
        Context = report.Context,
    };
}
