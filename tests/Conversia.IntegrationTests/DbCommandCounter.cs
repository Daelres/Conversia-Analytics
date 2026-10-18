using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Conversia.IntegrationTests;

/// <summary>
/// Counts every command EF Core sends to PostgreSQL, so a test can prove that a
/// request never reached the database (a delta of zero) without mocking anything.
/// The one command not counted is the <c>DELETE</c> on <c>ErrorLogs</c>, which the retention purge sends at every host's
/// startup (it is background bookkeeping, not what these tests measure); an <c>INSERT</c>, a <c>SELECT</c> or any other command
/// on any table is counted, so "zero commands at startup" stays an honest claim. The error registry suites count the rest
/// with their own <see cref="CommandInterceptor"/>.
/// </summary>
internal sealed class DbCommandCounter : DbCommandInterceptor
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    private void CountIfRelevant(DbCommand command)
    {
        if (!IsStartupRetentionPurge(command.CommandText))
        {
            Interlocked.Increment(ref _count);
        }
    }

    private static bool IsStartupRetentionPurge(string commandText)
        => commandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) && commandText.Contains("ErrorLogs", StringComparison.Ordinal);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CountIfRelevant(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        CountIfRelevant(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CountIfRelevant(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        CountIfRelevant(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        CountIfRelevant(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        CountIfRelevant(command);
        return base.ScalarExecuting(command, eventData, result);
    }
}
