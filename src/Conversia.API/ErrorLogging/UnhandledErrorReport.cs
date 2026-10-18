using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Text.Json;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;

namespace Conversia.API.ErrorLogging;

/// <summary>Describes an unexpected failure of a request as an <see cref="ErrorReport"/>: what is needed to start debugging, and nothing the caller sent in a body, header or cookie.</summary>
internal static class UnhandledErrorReport
{
    private const string TenantIdClaimType = "tenant_id";

    private static readonly string ServiceVersion = ReadServiceVersion();

    public static ErrorReport From(HttpContext context, Exception exception, string environmentName, DateTime utcNow)
    {
        var request = context.Request;
        var webhook = RequestPathRedactor.IsWebhookPath(request.Path.Value);
        return new ErrorReport
        {
            OccurredAt = utcNow,
            Source = ErrorSource.Api,
            Kind = ErrorKind.Unhandled,
            Environment = environmentName,
            ServiceVersion = ServiceVersion,
            Instance = Environment.MachineName,
            HttpMethod = request.Method,
            Path = RequestPathRedactor.Redact(request.Path.Value),
            Query = request.QueryString.Value,
            Endpoint = context.GetEndpoint()?.DisplayName,
            RouteValues = RouteValuesJson(request),
            StatusCode = StatusCodes.Status500InternalServerError,
            TraceId = Activity.Current?.TraceId.ToHexString(),
            RequestId = context.TraceIdentifier,
            UserId = GuidClaim(context, JwtRegisteredClaimNames.Sub),
            TenantId = GuidClaim(context, TenantIdClaimType),
            UserAgent = request.Headers.UserAgent.ToString(),
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            // A webhook failure can quote the customer's data (a database error with the offending value): only the masked description is kept.
            ExceptionMessage = webhook ? WebhookErrors.Describe(exception) : exception.Message,
            ExceptionDetails = webhook ? null : exception.ToString(),
        };
    }

    /// <summary>What can be said of a failure whose own description cannot be read (a <c>Message</c> or <c>ToString</c> that throws): the exception type and nothing else.</summary>
    public static ErrorReport Minimal(Exception exception, string environmentName, DateTime utcNow)
        => new()
        {
            OccurredAt = utcNow,
            Source = ErrorSource.Api,
            Kind = ErrorKind.Unhandled,
            Environment = environmentName,
            ServiceVersion = ServiceVersion,
            Instance = Environment.MachineName,
            StatusCode = StatusCodes.Status500InternalServerError,
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
        };

    private static string? RouteValuesJson(HttpRequest request)
        => request.RouteValues.Count == 0
            ? null
            : JsonSerializer.Serialize(RequestPathRedactor.RedactRouteValues(request.RouteValues));

    private static Guid? GuidClaim(HttpContext context, string claimType)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return Guid.TryParse(context.User.FindFirst(claimType)?.Value, out var id) ? id : null;
    }

    // "1.2.3+3f2a9c1" -> "1.2.3": the build metadata after '+' is the commit, which is a different thing from the version.
    private static string ReadServiceVersion()
    {
        var assembly = typeof(UnhandledErrorReport).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (informational ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
    }
}
