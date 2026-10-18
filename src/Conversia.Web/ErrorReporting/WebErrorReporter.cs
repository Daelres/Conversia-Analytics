using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.ErrorLogging;
using Conversia.Domain.Common;
using Microsoft.Extensions.Options;

namespace Conversia.Web.ErrorReporting;

/// <summary>
/// Gives an unexpected failure of the Web its code and tells the API about it, best effort: one attempt, bounded by
/// <c>ErrorReporting:TimeoutSeconds</c>, no retries. Whatever happens the caller gets a well-formed code, and the failure is logged with it.
/// </summary>
internal sealed class WebErrorReporter(
    IHttpClientFactory httpClientFactory,
    IOptions<ErrorReportingOptions> options,
    ErrorStormGuard stormGuard,
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<WebErrorReporter> logger) : IWebErrorReporter
{
    public const string HttpClientName = "ErrorReporting";
    public const string KeyHeader = "X-Error-Report-Key";
    public const string ReportPath = "api/diagnostics/errors";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<string> ReportAsync(WebErrorReport report, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var code = ErrorCode.New(now);
        var outcome = "not reported";
        var failure = (Exception?)null;

        try
        {
            var settings = options.Value;
            if (!settings.CanReport)
            {
                outcome = "not reported: error reporting is off or has no key";
            }
            else
            {
                var sanitized = ErrorReportSanitizer.Sanitize(WebErrorReportBuilder.Build(report, report.HttpContext ?? httpContextAccessor.HttpContext, environment.EnvironmentName, code, now), now);
                (code, outcome) = stormGuard.TryAcquire(sanitized.Fingerprint!)
                    ? await SendAsync(sanitized, settings, code, cancellationToken)
                    : (code, "not reported: storm control, too many occurrences of this defect in a minute");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            outcome = "not reported: the report could not be sent";
        }

        Log(report, code, outcome, failure);
        return code;
    }

    private async Task<(string Code, string Outcome)> SendAsync(ErrorReport sanitized, ErrorReportingOptions settings, string code, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, ReportPath) { Content = JsonContent.Create(ToRequest(sanitized), options: Json) };
        request.Headers.Add(KeyHeader, settings.ApiKey);

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            return (code, $"not reported: the API answered {(int)response.StatusCode}");
        }

        var answer = await response.Content.ReadFromJsonAsync<ReportErrorResponse>(Json, deadline.Token);
        return ErrorCode.IsValid(answer?.Code) ? (answer!.Code, "reported") : (code, "reported, but the API's answer had no usable code");
    }

    private static ReportErrorRequest ToRequest(ErrorReport report) => new()
    {
        Code = report.Code,
        Kind = report.Kind.ToString(),
        HttpMethod = report.HttpMethod,
        Path = report.Path ?? string.Empty,
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
        Context = report.Context,
        Environment = report.Environment,
        ServiceVersion = report.ServiceVersion,
        Instance = report.Instance,
        OccurredAt = report.OccurredAt,
    };

    // The log is the record of last resort, so it always carries the code and the failure; a provider that cannot format a
    // hostile exception must not turn into a failure of the report.
    private void Log(WebErrorReport report, string code, string outcome, Exception? reportFailure)
    {
        try
        {
            logger.LogError(report.Exception, "Web error {ErrorId} ({Kind}, {Outcome}). ReportFailure={ReportFailure}",
                code, report.Kind, outcome, reportFailure?.GetType().FullName);
        }
        catch (Exception)
        {
            try
            {
                logger.LogError("Web error {ErrorId} ({Kind}, {Outcome}); the exception {ExceptionType} could not be logged.",
                    code, report.Kind, outcome, report.Exception?.GetType().FullName);
            }
            catch (Exception)
            {
                // Nothing left to try: the caller still gets the code.
            }
        }
    }
}
