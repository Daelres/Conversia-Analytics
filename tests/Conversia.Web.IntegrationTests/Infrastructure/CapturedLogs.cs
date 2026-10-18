using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Collects everything the Web host logs (message + exception text) so tests can prove secrets never reach a log.
/// This provider accepts every level; <see cref="WebTestHost"/> clears the host's logger filter rules and sets the
/// minimum level to Trace so that the host actually delivers Debug/Trace entries to it.
/// </summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    /// <summary>The same entries with their level and structured properties (a value logged as a property is not in the rendered message).</summary>
    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    /// <summary>The records that mention <paramref name="value"/> in their message, exception or any property.</summary>
    public IEnumerable<LogRecord> Mentioning(string value) => Records.Where(r => r.Mentions(value));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, _records);

    public void Dispose()
    {
    }

    /// <summary>The text of an exception; an exception whose own <c>ToString</c> fails (some do) must not break the capture.</summary>
    private static string Describe(Exception? exception)
    {
        try
        {
            return exception?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return $"{exception!.GetType().FullName} (its text could not be read)";
        }
    }

    internal sealed record LogRecord(LogLevel Level, string Category, string Message, string Exception, IReadOnlyList<KeyValuePair<string, string>> Properties)
    {
        /// <summary>Message, exception and every property value in one string.</summary>
        public string FullText => $"{Message} {Exception} {string.Join(' ', Properties.Select(p => $"{p.Key}={p.Value}"))}";

        public bool Mentions(string value) => FullText.Contains(value, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink, ConcurrentQueue<LogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var exceptionText = Describe(exception);
            sink.Enqueue($"{category}: {message} {exceptionText}");
            var properties = new List<KeyValuePair<string, string>>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                properties.AddRange(pairs
                    .Where(p => p.Key != "{OriginalFormat}")
                    .Select(p => new KeyValuePair<string, string>(p.Key, Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)));
            }

            records.Enqueue(new LogRecord(logLevel, category, message, exceptionText, properties));
        }
    }
}
