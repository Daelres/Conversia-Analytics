using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Domain.Enums;
using Conversia.Web.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace Conversia.Web.ErrorReporting;

/// <summary>
/// Describes a failure of the Web as an <see cref="ErrorReport"/>: what is needed to start debugging and nothing a person sent in a
/// body, header or cookie. Reading the description of a hostile exception can throw, so every such read is guarded.
/// </summary>
internal static class WebErrorReportBuilder
{
    private const string NoRequestPath = "/";

    private static readonly string ServiceVersion = ReadServiceVersion();

    public static ErrorReport Build(WebErrorReport report, HttpContext? context, string environmentName, string code, DateTime utcNow)
    {
        var request = context?.Request;
        var handled = context?.Features.Get<IExceptionHandlerPathFeature>();
        var exception = report.Exception;

        return new ErrorReport
        {
            Code = code,
            OccurredAt = utcNow,
            Source = ErrorSource.Web,
            Kind = report.Kind,
            Environment = environmentName,
            ServiceVersion = ServiceVersion,
            Instance = Environment.MachineName,
            HttpMethod = request?.Method,
            Path = PathOf(handled, request),
            Query = request?.QueryString.Value,
            Endpoint = (handled?.Endpoint ?? context?.GetEndpoint())?.DisplayName,
            RouteValues = RouteValuesJson(handled?.RouteValues ?? request?.RouteValues),
            StatusCode = report.StatusCode ?? (report.Kind == ErrorKind.Unhandled ? StatusCodes.Status500InternalServerError : StatusCodes.Status503ServiceUnavailable),
            TraceId = Activity.Current?.TraceId.ToHexString(),
            RequestId = context?.TraceIdentifier,
            UserId = GuidClaim(context, System.Security.Claims.ClaimTypes.NameIdentifier),
            TenantId = GuidClaim(context, SessionCookieExtensions.TenantIdClaimType),
            UserAgent = request?.Headers.UserAgent.ToString(),
            ExceptionType = exception is null ? report.Kind.ToString() : exception.GetType().FullName ?? exception.GetType().Name,
            ExceptionMessage = exception is null ? StatusMessage(report) : Guarded(() => exception.Message),
            ExceptionDetails = exception is null ? null : Guarded(exception.ToString),
            Context = UpstreamJson(report),
        };
    }

    private static string PathOf(IExceptionHandlerPathFeature? handled, HttpRequest? request)
    {
        var path = handled?.Path ?? request?.Path.Value;
        return string.IsNullOrEmpty(path) ? NoRequestPath : path;
    }

    private static string? StatusMessage(WebErrorReport report)
        => report.StatusCode is { } status ? $"The API answered {status}." : null;

    private static string? Guarded(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? RouteValuesJson(RouteValueDictionary? values)
        => values is null || values.Count == 0
            ? null
            : JsonSerializer.Serialize(values.ToDictionary(v => v.Key, v => v.Value?.ToString()));

    private static string? UpstreamJson(WebErrorReport report)
        => report.UpstreamMethod is null && report.UpstreamPath is null
            ? null
            : JsonSerializer.Serialize(new { upstreamMethod = report.UpstreamMethod, upstreamPath = report.UpstreamPath });

    private static Guid? GuidClaim(HttpContext? context, string claimType)
    {
        if (context?.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return Guid.TryParse(context.User.FindFirst(claimType)?.Value, out var id) ? id : null;
    }

    // "1.2.3+3f2a9c1" -> "1.2.3": the build metadata after '+' is the commit, which is a different thing from the version.
    private static string ReadServiceVersion()
    {
        var assembly = typeof(WebErrorReportBuilder).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (informational ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
    }
}
