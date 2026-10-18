namespace Conversia.Domain.Enums;

/// <summary>What kind of unexpected failure was registered. Stored as its integer value.</summary>
public enum ErrorKind
{
    Unhandled = 1,
    UpstreamUnavailable = 2,
    UpstreamBadResponse = 3,
}
