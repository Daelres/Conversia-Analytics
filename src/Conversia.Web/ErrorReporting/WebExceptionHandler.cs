using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Diagnostics;

namespace Conversia.Web.ErrorReporting;

/// <summary>
/// Runs for an exception nobody handled, before the exception-handler middleware re-executes <c>/Error</c>: registers it, and hands the
/// code to that page (in <c>HttpContext.Items</c>) and to the caller (<c>X-Error-Id</c>). It never handles the exception itself, so the
/// error page and its 500 are the middleware's. A request the browser abandoned is nobody's error, and a response that has already started
/// can no longer carry a code (the middleware logs it and rethrows).
/// </summary>
internal sealed class WebExceptionHandler(IWebErrorReporter reporter) : IExceptionHandler
{
    public const string ErrorIdHeader = "X-Error-Id";

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted || IsAbandoned(context, exception))
        {
            return false;
        }

        var code = await reporter.ReportAsync(new WebErrorReport(ErrorKind.Unhandled, exception, context), CancellationToken.None);
        context.Items[ErrorCodeState.ItemKey] = code;
        context.Response.Headers[ErrorIdHeader] = code;
        return false;
    }

    private static bool IsAbandoned(HttpContext context, Exception exception)
        => exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested;
}
