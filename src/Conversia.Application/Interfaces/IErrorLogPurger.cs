namespace Conversia.Application.Interfaces;

public interface IErrorLogPurger
{
    /// <summary>Deletes the rows whose <c>OccurredAt</c> is older than <paramref name="olderThanUtc"/>, in batches of 1 000; returns how many.</summary>
    Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);
}
