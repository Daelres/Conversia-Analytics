using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Conversia.IntegrationTests;

/// <summary>
/// Everything a host logged: level, category, the formatted message and exception text (<see cref="Entry.Text"/>), and the
/// structured properties and scopes that came with it (<see cref="Entry.Properties"/>, <see cref="Entry.FullText"/>).
/// <para><see cref="Entry.Text"/> is only what the logger rendered (message plus exception); a value that was logged as a structured
/// property, such as the <c>ErrorId</c> of the registry, is not in it unless the message template prints it. <see cref="Entry.FullText"/>
/// adds every property and scope value, so use it (or <see cref="Entry.HasPropertyValue"/>) to ask "was this value logged at all?".</para>
/// </summary>
internal sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<Entry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    internal sealed record Entry(
        LogLevel Level, string Category, string Text, string Message, string Exception, IReadOnlyList<KeyValuePair<string, string>> Properties)
    {
        /// <summary>Message, exception and every property/scope value in one string.</summary>
        public string FullText => $"{Message} {Exception} {string.Join(' ', Properties.Select(p => $"{p.Key}={p.Value}"))}";

        public bool Mentions(string value) => FullText.Contains(value, StringComparison.Ordinal);

        public bool HasPropertyValue(string value) => Properties.Any(p => p.Value == value);
    }

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IEnumerable<Entry> Mentioning(string value) => Entries.Where(e => e.Mentions(value));

    /// <summary>Waits (bounded) until an entry matching <paramref name="predicate"/> exists, then returns it; fails the test otherwise.</summary>
    public async Task<Entry> WaitForAsync(Func<Entry, bool> predicate, string description, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var found = Entries.FirstOrDefault(predicate);
            if (found is not null)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"No log entry ({description}) within {timeoutSeconds}s. Warnings and errors seen: {string.Join(" | ", Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => Truncate(e.FullText)).Take(10))}");
            }

            await Task.Delay(50);
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    private sealed class CapturingLogger(string category, ConcurrentQueue<Entry> sink, Func<IExternalScopeProvider> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new List<KeyValuePair<string, string>>();
            AddProperties(state, properties);
            scopes().ForEachScope((scope, list) => AddProperties(scope, list), properties);
            var message = formatter(state, exception);
            sink.Enqueue(new Entry(logLevel, category, $"{message} {exception}", message, exception?.ToString() ?? string.Empty, properties));
        }

        private static void AddProperties(object? state, List<KeyValuePair<string, string>> into)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key != "{OriginalFormat}")
                    {
                        into.Add(new KeyValuePair<string, string>(pair.Key, Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                    }
                }
            }
        }
    }
}
