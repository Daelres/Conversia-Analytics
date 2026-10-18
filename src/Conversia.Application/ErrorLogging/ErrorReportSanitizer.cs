using System.Text.RegularExpressions;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;

namespace Conversia.Application.ErrorLogging;

/// <summary>
/// Turns whatever a reporter sent into what may be stored: every text sanitized and cut to its column, a fingerprint, a clock
/// that cannot put a row outside the retention window, and a code only if it is well formed. Applied to every report, whatever its source.
/// </summary>
public static partial class ErrorReportSanitizer
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastTolerance = TimeSpan.FromDays(1);

    public static ErrorReport Sanitize(ErrorReport report, DateTime utcNow)
    {
        var exceptionType = ErrorSanitizer.SanitizeText(report.ExceptionType, ErrorLog.ExceptionTypeMaxLength) ?? string.Empty;
        var details = ErrorSanitizer.SanitizeText(report.ExceptionDetails, ErrorLog.ExceptionDetailsMaxLength);

        return report with
        {
            Code = ErrorCode.IsValid(report.Code) ? report.Code : null,
            OccurredAt = NormalizeOccurredAt(report.OccurredAt, utcNow),
            Environment = ErrorSanitizer.SanitizeText(report.Environment, ErrorLog.EnvironmentMaxLength),
            ServiceVersion = ErrorSanitizer.SanitizeText(report.ServiceVersion, ErrorLog.ServiceVersionMaxLength),
            Instance = ErrorSanitizer.SanitizeText(report.Instance, ErrorLog.InstanceMaxLength),
            HttpMethod = ErrorSanitizer.SanitizeText(report.HttpMethod, ErrorLog.HttpMethodMaxLength),
            Path = ErrorSanitizer.SanitizeText(report.Path, ErrorLog.PathMaxLength),
            Query = ErrorSanitizer.SanitizeQuery(report.Query, ErrorLog.QueryMaxLength),
            Endpoint = ErrorSanitizer.SanitizeText(report.Endpoint, ErrorLog.EndpointMaxLength),
            RouteValues = ErrorSanitizer.SanitizeText(report.RouteValues, ErrorLog.RouteValuesMaxLength),
            TraceId = ErrorSanitizer.SanitizeText(report.TraceId, ErrorLog.TraceIdMaxLength),
            RequestId = ErrorSanitizer.SanitizeText(report.RequestId, ErrorLog.RequestIdMaxLength),
            UserAgent = ErrorSanitizer.SanitizeText(report.UserAgent, ErrorLog.UserAgentMaxLength),
            ExceptionType = exceptionType,
            ExceptionMessage = ErrorSanitizer.SanitizeText(report.ExceptionMessage, ErrorLog.ExceptionMessageMaxLength),
            ExceptionDetails = details,
            Context = ErrorSanitizer.SanitizeText(report.Context, ErrorLog.ContextMaxLength),
            Fingerprint = IsFingerprint(report.Fingerprint) ? report.Fingerprint : ErrorFingerprint.Compute(exceptionType, details),
        };
    }

    // The reporter's clock is not trusted: a date in the far past would be purged at once, one in the far future never.
    private static DateTime NormalizeOccurredAt(DateTime reported, DateTime utcNow)
    {
        var value = reported.Kind switch
        {
            DateTimeKind.Local => reported.ToUniversalTime(),
            _ => DateTime.SpecifyKind(reported, DateTimeKind.Utc),
        };

        return value < utcNow - PastTolerance || value > utcNow + FutureTolerance ? utcNow : value;
    }

    private static bool IsFingerprint(string? value) => value is not null && FingerprintShape().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintShape();
}
