using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.ErrorLogging;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.ErrorLogging;

/// <summary>
/// Records unexpected errors. Runs on the error path of a request, so it never throws, never reads the database (except to
/// resolve a code collision), and writes with a context of its own: the request's one may be exactly what failed.
/// </summary>
public sealed class ErrorRegistry(
    IServiceScopeFactory scopeFactory,
    ErrorLoggingOptions options,
    ErrorStormGuard stormGuard,
    TimeProvider timeProvider,
    ILogger<ErrorRegistry> logger) : IErrorRegistry
{
    private const int MaxCodeAttempts = 5;

    public async Task<ErrorRegistration> RegisterAsync(ErrorReport report, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var code = ErrorCode.IsValid(report.Code) ? report.Code! : ErrorCode.New(now);

        try
        {
            var sanitized = ErrorReportSanitizer.Sanitize(report, now);
            code = ErrorCode.IsValid(report.Code) ? report.Code! : ErrorCode.New(sanitized.OccurredAt);
            return await RegisterSanitizedAsync(sanitized, code, cancellationToken);
        }
        catch (Exception ex)
        {
            // Only the code and the failure of the registry itself: the report content is unsanitized here (the sanitizer may be what failed).
            logger.LogError(ex, "Error {ErrorCode} could not be registered ({Reason}). FailureType={FailureType}",
                code, "the registry itself failed", ex.GetType().FullName ?? ex.GetType().Name);
            return new ErrorRegistration(code, Persisted: false);
        }
    }

    private async Task<ErrorRegistration> RegisterSanitizedAsync(ErrorReport report, string code, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            LogNotStored(LogLevel.Warning, code, report, "ErrorLogging:Enabled is false", exception: null);
            return new ErrorRegistration(code, Persisted: false);
        }

        if (!stormGuard.TryAcquire(report.Fingerprint!))
        {
            LogNotStored(LogLevel.Error, code, report, "storm control: too many occurrences of this fingerprint in a minute", exception: null);
            return new ErrorRegistration(code, Persisted: false);
        }

        return await PersistAsync(report, code, cancellationToken);
    }

    private async Task<ErrorRegistration> PersistAsync(ErrorReport report, string code, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.PersistTimeoutSeconds));

        try
        {
            code = await InsertAsync(report, code, deadline.Token);
            return new ErrorRegistration(code, Persisted: true);
        }
        catch (Exception ex)
        {
            var reason = deadline.IsCancellationRequested ? "the database did not answer in time" : "the database refused the insert";
            LogNotStored(LogLevel.Error, code, report, reason, ex);
            return new ErrorRegistration(code, Persisted: false);
        }
    }

    // The unique index on Code arbitrates; a collision (a code chosen by the reporter, or a rare draw) is retried with a new code.
    private async Task<string> InsertAsync(ErrorReport report, string code, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.ErrorLogs.Add(ErrorLogMapper.ToEntity(report, code));
                await db.SaveChangesAsync(cancellationToken);
                return code;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation() && attempt < MaxCodeAttempts)
            {
                code = ErrorCode.New(report.OccurredAt);
            }
        }
    }

    private void LogNotStored(LogLevel level, string code, ErrorReport report, string reason, Exception? exception)
        => logger.Log(
            level,
            exception,
            "Error {ErrorCode} was not stored ({Reason}). Source={Source} Fingerprint={Fingerprint} Path={Path} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
            code, reason, report.Source, report.Fingerprint, report.Path, report.ExceptionType, report.ExceptionMessage);
}
