using Npgsql;

namespace Conversia.Infrastructure.Classification;

/// <summary>
/// How a failed classification is described in logs: the kind of failure and nothing else. While a conversation is being classified the exception
/// can come from the classifier or the database, and either may quote what it was reading (a regular expression that failed on a message, a
/// constraint that names a value), so its message is never logged: only the type of the exception, the type of the innermost one and, for
/// PostgreSQL, its SQLSTATE code.
/// </summary>
internal static class ClassificationErrors
{
    public static string Describe(Exception exception)
    {
        var innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        var description = exception.GetType().Name;
        if (!ReferenceEquals(innermost, exception))
        {
            description += $" caused by {innermost.GetType().Name}";
        }

        return innermost is PostgresException { SqlState: { Length: > 0 } sqlState } ? $"{description} (SQLSTATE {sqlState})" : description;
    }
}
