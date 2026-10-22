namespace Conversia.Application.Interfaces;

/// <summary>
/// A brake on guessing a platform administrator's password, per account (the per-address limit of the endpoint cannot stop a guesser who
/// changes address). It is keyed by the email as typed (normalized) whether or not an administrator has it, so the answer never says which
/// emails exist. The state is in memory: it is per process and gone on restart.
/// </summary>
public interface IAdminLoginThrottle
{
    /// <summary>How long the account must wait before the next attempt is looked at; null when it may be tried.</summary>
    TimeSpan? RetryAfter(string email);

    /// <summary>One more failed attempt on the account. Past the threshold the wait grows with each failure.</summary>
    void RecordFailure(string email);

    /// <summary>A successful sign-in clears the account's failures.</summary>
    void Reset(string email);
}
