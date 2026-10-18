using System.Collections.Concurrent;
using Conversia.Domain.Common;
using Conversia.Web.ErrorReporting;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>A stand-in for the Web's error reporter: records what the code under test asked it to report and answers with a code of its own.</summary>
internal sealed class RecordingReporter : IWebErrorReporter
{
    private readonly ConcurrentQueue<WebErrorReport> _reports = new();
    private readonly ConcurrentDictionary<WebErrorReport, string> _codes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The code to answer with; by default a fresh well-formed one per report.</summary>
    public Func<WebErrorReport, string> CodeFor { get; set; } = _ => ErrorCode.New(DateTime.UtcNow);

    /// <summary>Makes the reporter fail, which a reporter must never do; the code under test has to cope anyway.</summary>
    public Func<WebErrorReport, Exception?>? Failure { get; set; }

    public IReadOnlyList<WebErrorReport> Reports => _reports.ToArray();

    /// <summary>The code this reporter answered with for <paramref name="report"/>.</summary>
    public string CodeAnsweredFor(WebErrorReport report) => _codes[report];

    public Task<string> ReportAsync(WebErrorReport report, CancellationToken cancellationToken = default)
    {
        _reports.Enqueue(report);
        if (Failure?.Invoke(report) is { } failure)
        {
            throw failure;
        }

        var code = CodeFor(report);
        _codes[report] = code;
        return Task.FromResult(code);
    }
}

/// <summary>Lets a class that wants an <c>ILogger&lt;T&gt;</c> log into a <see cref="CapturedLogs"/>.</summary>
internal sealed class CapturedLogger<T>(CapturedLogs logs) : ILogger<T>
{
    private readonly ILogger _inner = logs.CreateLogger(typeof(T).FullName!);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
