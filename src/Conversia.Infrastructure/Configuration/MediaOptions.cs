namespace Conversia.Infrastructure.Configuration;

/// <summary>Where media is stored (<c>Media:Store</c>).</summary>
public enum MediaStoreKind
{
    /// <summary>No store: media is not downloaded, served or sent (the metadata is still kept).</summary>
    None,

    /// <summary>Encrypted files on a local directory: development and tests only.</summary>
    Local,

    /// <summary>A private Azure Blob Storage container.</summary>
    Azure,
}

/// <summary><c>Media:*</c> (docs/MESSAGING.md, "Media"; docs/DOCKER.md for the storage settings).</summary>
/// <param name="RetentionDays">The platform default of how long a stored copy is kept; a company may have its own (the administrator sets it).</param>
/// <param name="DownloadWindowHours">How long after a message arrived its media is still fetched (Meta keeps received media 7 days).</param>
/// <param name="MaxDownloadAttempts">Attempts before a download is marked failed.</param>
/// <param name="RetryBaseSeconds">The first retry waits this long; each next one twice as long, at most 6 hours.</param>
public sealed record MediaOptions(
    MediaStoreKind Store,
    int RetentionDays,
    bool DownloadEnabled,
    int DownloadIntervalSeconds,
    int DownloadWindowHours,
    int MaxDownloadAttempts,
    int RetryBaseSeconds,
    int LeaseMinutes,
    int RetentionIntervalMinutes,
    int RetentionBatchSize,
    int DownloadTimeoutSeconds = 120,
    int OrphanSweepIntervalHours = 24,
    int OrphanGraceHours = 24)
{
    public const string SectionName = "Media";

    public static MediaOptions Default { get; } = new(
        Store: MediaStoreKind.None, RetentionDays: 90, DownloadEnabled: true, DownloadIntervalSeconds: 10, DownloadWindowHours: 144, MaxDownloadAttempts: 8,
        RetryBaseSeconds: 60, LeaseMinutes: 10, RetentionIntervalMinutes: 60, RetentionBatchSize: 200);

    public TimeSpan RetryDelay(int attempts)
        => TimeSpan.FromSeconds(Math.Min(RetryBaseSeconds * Math.Pow(2, Math.Max(0, attempts - 1)), 6 * 3600));
}
