namespace Conversia.Domain.Enums;

/// <summary>Where the stored copy of a message's media stands (docs/MESSAGING.md, "Media"). Stored by name; null for a message without media.</summary>
public enum MediaStatus
{
    /// <summary>The file is waiting for the download job.</summary>
    Pending = 1,

    /// <summary>A copy is in the media store.</summary>
    Stored = 2,

    /// <summary>Bigger than the limit of its kind: not stored.</summary>
    TooLarge = 3,

    /// <summary>A type that is not allowed (an executable, a script, content that does not match its declared kind): not stored.</summary>
    Blocked = 4,

    /// <summary>The provider no longer has it (received media is kept 7 days), or it was not fetched in time.</summary>
    Expired = 5,

    /// <summary>Every attempt failed, or the message never carried a media id.</summary>
    Failed = 6,

    /// <summary>The stored copy was deleted when the company's media retention ran out.</summary>
    Purged = 7,
}
