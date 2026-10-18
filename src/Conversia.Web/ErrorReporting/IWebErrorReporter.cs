using Conversia.Domain.Enums;

namespace Conversia.Web.ErrorReporting;

/// <summary>Stub (tests first): what the Web knows about one unexpected failure it wants to register.</summary>
public sealed record WebErrorReport(
    ErrorKind Kind,
    Exception? Exception = null,
    HttpContext? HttpContext = null,
    int? StatusCode = null,
    string? UpstreamMethod = null,
    string? UpstreamPath = null);

/// <summary>Stub (tests first): registers an unexpected Web-side failure and hands back the code the user is shown.</summary>
public interface IWebErrorReporter
{
    /// <summary>Always returns a well-formed <c>ERR-yyyyMMdd-XXXXXX</c> code; never throws.</summary>
    Task<string> ReportAsync(WebErrorReport report, CancellationToken cancellationToken = default);
}
