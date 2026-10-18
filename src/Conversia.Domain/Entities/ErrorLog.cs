using Conversia.Domain.Common;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Entities;

/// <summary>
/// One registered unexpected error. An operations table: it carries the tenant and user of the failing request as data
/// but is never filtered by tenant. Every text is stored already sanitized and cut to the limits below.
/// </summary>
public class ErrorLog : BaseEntity
{
    public const int CodeMaxLength = 24;
    public const int EnvironmentMaxLength = 50;
    public const int ServiceVersionMaxLength = 50;
    public const int InstanceMaxLength = 100;
    public const int HttpMethodMaxLength = 10;
    public const int PathMaxLength = 500;
    public const int QueryMaxLength = 1000;
    public const int EndpointMaxLength = 300;
    public const int RouteValuesMaxLength = 1000;
    public const int TraceIdMaxLength = 64;
    public const int RequestIdMaxLength = 100;
    public const int UserAgentMaxLength = 300;
    public const int ExceptionTypeMaxLength = 300;
    public const int ExceptionMessageMaxLength = 2000;
    public const int ExceptionDetailsMaxLength = 16000;
    public const int FingerprintLength = 16;
    public const int ContextMaxLength = 2000;

    public string Code { get; init; } = string.Empty;

    public DateTime OccurredAt { get; init; }

    public ErrorSource Source { get; init; }

    public ErrorKind Kind { get; init; }

    public string? Environment { get; init; }

    public string? ServiceVersion { get; init; }

    public string? Instance { get; init; }

    public string? HttpMethod { get; init; }

    public string? Path { get; init; }

    public string? Query { get; init; }

    public string? Endpoint { get; init; }

    public string? RouteValues { get; init; }

    public int StatusCode { get; init; }

    public string? TraceId { get; init; }

    public string? RequestId { get; init; }

    public Guid? UserId { get; init; }

    public Guid? TenantId { get; init; }

    public string? UserAgent { get; init; }

    public string ExceptionType { get; init; } = string.Empty;

    public string? ExceptionMessage { get; init; }

    public string? ExceptionDetails { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public string? Context { get; init; }
}
