using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Diagnostics;

/// <summary>Everything known about one unexpected error, as handed to <see cref="Interfaces.IErrorRegistry"/>.</summary>
public sealed record ErrorReport
{
    /// <summary>A code chosen by the reporter; honored only when valid and unused.</summary>
    public string? Code { get; init; }

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

    /// <summary>Computed by the registry when absent.</summary>
    public string? Fingerprint { get; init; }

    public string? Context { get; init; }
}
