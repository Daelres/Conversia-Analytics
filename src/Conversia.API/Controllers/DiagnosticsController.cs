using Conversia.API.ErrorLogging;
using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/diagnostics")]
[AllowAnonymous]
public class DiagnosticsController(IErrorRegistry errorRegistry, TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// The Web reports its own errors here (it has no database). Guarded by a shared key instead of a JWT, and by its own rate
    /// limit, which runs before the key is checked so that guessing the key is throttled as well.
    /// </summary>
    [HttpPost("errors")]
    [EnableRateLimiting(ErrorReportRateLimitingExtensions.PolicyName)]
    [ErrorReportAccess]
    public async Task<ActionResult<ReportErrorResponse>> ReportError(ReportErrorRequest request, CancellationToken cancellationToken)
    {
        var registration = await errorRegistry.RegisterAsync(ToReport(request, timeProvider.GetUtcNow().UtcDateTime), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new ReportErrorResponse(registration.Code, registration.Persisted));
    }

    // The source is the Web whatever the payload claims, and the API computes the fingerprint itself.
    private static ErrorReport ToReport(ReportErrorRequest request, DateTime utcNow) => new()
    {
        Code = request.Code,
        OccurredAt = request.OccurredAt ?? utcNow,
        Source = ErrorSource.Web,
        Kind = Enum.Parse<ErrorKind>(request.Kind, ignoreCase: true),
        Environment = request.Environment,
        ServiceVersion = request.ServiceVersion,
        Instance = request.Instance,
        HttpMethod = request.HttpMethod,
        Path = request.Path,
        Query = request.Query,
        Endpoint = request.Endpoint,
        RouteValues = request.RouteValues,
        StatusCode = request.StatusCode ?? 0,
        TraceId = request.TraceId,
        RequestId = request.RequestId,
        UserId = request.UserId,
        TenantId = request.TenantId,
        UserAgent = request.UserAgent,
        ExceptionType = request.ExceptionType,
        ExceptionMessage = request.ExceptionMessage,
        ExceptionDetails = request.ExceptionDetails,
        Context = request.Context,
    };
}
