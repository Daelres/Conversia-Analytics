using Conversia.API.ErrorLogging;
using Conversia.API.Localization;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private const string ProblemJsonContentType = "application/problem+json";
    private const string ErrorIdExtension = "errorId";

    /// <summary>Response header with the code of the registered error, for clients that do not read the body.</summary>
    public const string ErrorIdHeader = "X-Error-Id";

    private const int ClientClosedRequest = 499;

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;

    public ExceptionHandlingMiddleware(
        RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment environment, TimeProvider timeProvider)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public async Task InvokeAsync(HttpContext context, IErrorRegistry errorRegistry)
    {
        try
        {
            await _next(context);
        }
        catch (DuplicateEmailException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemTexts.DuplicateEmailTitle, ex.Message);
        }
        catch (InvalidCredentialsException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, ProblemTexts.InvalidCredentialsTitle, ex.Message);
        }
        catch (UserNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.UserNotFoundTitle, ex.Message);
        }
        catch (BranchNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.BranchNotFoundTitle, ex.Message);
        }
        catch (AdvisorNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.AdvisorNotFoundTitle, ex.Message);
        }
        catch (ChannelNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.ChannelNotFoundTitle, ex.Message);
        }
        catch (TenantNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.TenantNotFoundTitle, ex.Message);
        }
        catch (TenantStatusConflictException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemTexts.TenantStatusConflictTitle, ex.Message);
        }
        catch (LineQuotaBelowActiveException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemTexts.LineQuotaConflictTitle, ex.Message);
        }
        catch (LineQuotaFullException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemTexts.LineQuotaFullTitle, ex.Message);
        }
        catch (TenantNotActiveException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, ProblemTexts.TenantNotActiveTitle, ex.Message,
                ("code", TenantNotActiveException.Code), ("tenantStatus", ex.Status.ToString()));
        }
        catch (ConversationNotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ProblemTexts.ConversationNotFoundTitle, ex.Message);
        }
        catch (DuplicateBranchNameException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemTexts.DuplicateBranchNameTitle, ex.Message);
        }
        catch (MetricsQueryTimeoutException ex)
        {
            // Expected under load, not a defect: no error code, no registry, and nothing of the statement in the log.
            _logger.LogWarning("A metrics query exceeded its time limit and was cancelled: {Method} {Path}.", context.Request.Method, RequestPathRedactor.Redact(context.Request.Path.Value));
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, ProblemTexts.QueryTimeoutTitle, ex.Message);
        }
        catch (DomainException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, ProblemTexts.InvalidRequestTitle, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nobody to answer, and not a defect to register.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
        catch (BadHttpRequestException ex)
        {
            await WriteBadRequestAsync(context, ex);
        }
        catch (Exception ex)
        {
            await HandleUnexpectedAsync(context, ex, errorRegistry);
        }
    }

    private async Task HandleUnexpectedAsync(HttpContext context, Exception exception, IErrorRegistry errorRegistry)
    {
        var code = await RegisterAsync(errorRegistry, context, exception);

        LogUnexpected(context, exception, code);

        if (context.Response.HasStarted)
        {
            // Headers and part of the body are already out: all that is left is to make the client see the response as broken.
            context.Abort();
            return;
        }

        context.Response.Headers[ErrorIdHeader] = code;

        // Never leak the exception message or stack trace to the caller.
        await WriteProblemAsync(
            context,
            StatusCodes.Status500InternalServerError,
            ProblemTexts.UnexpectedTitle,
            ProblemTexts.UnexpectedDetail,
            (ErrorIdExtension, code));
    }

    // Neither the registry nor the description of the exception may keep the caller from getting a code: reading Message or ToString
    // of a hostile exception can throw, and so can the registry. Whatever fails, the error is still answered, with a fresh code.
    private async Task<string> RegisterAsync(IErrorRegistry errorRegistry, HttpContext context, Exception exception)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        ErrorReport report;
        try
        {
            report = UnhandledErrorReport.From(context, exception, _environment.EnvironmentName, now);
        }
        catch (Exception ex)
        {
            _logger.LogError("The report of an unexpected {ExceptionType} could not be built ({FailureType}); a minimal one is registered.",
                TypeNameOf(exception), TypeNameOf(ex));
            report = UnhandledErrorReport.Minimal(exception, _environment.EnvironmentName, now);
        }

        try
        {
            return (await errorRegistry.RegisterAsync(report, CancellationToken.None)).Code;
        }
        catch (Exception ex)
        {
            var code = ErrorCode.New(report.OccurredAt);
            _logger.LogError(ex, "The error registry failed for {ErrorId}; the error is only in the log.", code);
            return code;
        }
    }

    // The log providers format the exception (Message, ToString): a hostile one must not turn into a failure of the response.
    private void LogUnexpected(HttpContext context, Exception exception, string code)
    {
        try
        {
            if (RequestPathRedactor.IsWebhookPath(context.Request.Path.Value))
            {
                // The raw exception of a webhook call can quote the customer's data; the masked description cannot.
                _logger.LogError("Unhandled exception {ErrorId} processing {Method} {Path}: {Error}",
                    code, context.Request.Method, RequestPathRedactor.Redact(context.Request.Path.Value), WebhookErrors.Describe(exception));
                return;
            }

            _logger.LogError(exception, "Unhandled exception {ErrorId} processing {Method} {Path}", code, context.Request.Method, RequestPathRedactor.Redact(context.Request.Path.Value));
        }
        catch (Exception)
        {
            try
            {
                _logger.LogError("Unhandled {ExceptionType} {ErrorId} processing {Method} {Path}; the exception itself could not be logged.",
                    TypeNameOf(exception), code, context.Request.Method, RequestPathRedactor.Redact(context.Request.Path.Value));
            }
            catch (Exception)
            {
                // Nothing left to try: the caller still gets the code.
            }
        }
    }

    private static string TypeNameOf(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    private static Task WriteBadRequestAsync(HttpContext context, BadHttpRequestException exception)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var (title, detail) = exception.StatusCode switch
        {
            StatusCodes.Status408RequestTimeout => (ProblemTexts.RequestTimeoutTitle, ProblemTexts.RequestTimeoutDetail),
            StatusCodes.Status413PayloadTooLarge => (ProblemTexts.PayloadTooLargeTitle, ProblemTexts.PayloadTooLargeDetail),
            _ => (ProblemTexts.InvalidRequestTitle, ProblemTexts.MalformedRequestDetail),
        };

        var status = exception.StatusCode is >= 400 and < 500 ? exception.StatusCode : StatusCodes.Status400BadRequest;
        return WriteProblemAsync(context, status, title, detail);
    }

    private static Task WriteProblemAsync(
        HttpContext context, int statusCode, string title, string detail, params (string Name, object Value)[] extensions)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        };

        foreach (var (name, value) in extensions)
        {
            problemDetails.Extensions[name] = value;
        }

        context.Response.StatusCode = statusCode;

        // WriteAsJsonAsync overwrites a pre-set ContentType with
        // application/json, so the problem media type must be passed to it.
        return context.Response.WriteAsJsonAsync(problemDetails, options: null, contentType: ProblemJsonContentType);
    }
}
