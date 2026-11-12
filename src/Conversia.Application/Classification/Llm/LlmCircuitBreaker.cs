using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Application.Classification.Llm;

public enum BreakerState
{
    /// <summary>Calls go through.</summary>
    Closed,

    /// <summary>Too many transport failures in a row (or a misconfiguration): no call until the cooldown ends.</summary>
    Open,

    /// <summary>The cooldown ended: one probe call at a time decides whether to close again.</summary>
    HalfOpen,
}

public enum BreakerDenial
{
    None,

    /// <summary>The breaker is open; <see cref="BreakerTicket.ReopensAt"/> says until when.</summary>
    Open,

    /// <summary>The breaker is half-open and another conversation is the probe.</summary>
    ProbeInFlight,
}

/// <param name="Admitted">Whether the call may be made.</param>
/// <param name="IsProbe">The admitted call is the half-open probe.</param>
/// <param name="ReopensAt">With <see cref="BreakerDenial.Open"/>: the end of the cooldown.</param>
public readonly record struct BreakerTicket(bool Admitted, bool IsProbe, BreakerDenial Denial, DateTime? ReopensAt);

/// <summary>
/// The circuit breaker of the LLM path (design D21, "Fallback and retry"). Process-local and thread-safe.
/// <list type="bullet">
/// <item>Only a <b>usable answer</b> (<see cref="RecordSuccess"/>) resets the count of consecutive transport failures and closes it.</item>
/// <item>A <b>transport</b> failure (timeout, rate limit, outage) counts; at <c>failureThreshold</c> in a row it opens for <c>cooldown</c>.</item>
/// <item>A <b>misconfiguration</b> (bad key, no permission, unknown model) opens it at once: every call would fail the same way.</item>
/// <item>An <b>unusable answer</b> (refused, truncated, invalid) neither counts nor resets: the caller ends its ticket with <see cref="Abandon"/>.</item>
/// <item>After the cooldown ONE probe at a time is admitted; every other conversation is told the probe is in flight.</item>
/// </list>
/// Every admitted ticket must end in <see cref="RecordSuccess"/>, <see cref="RecordTransportFailure"/>, <see cref="RecordMisconfiguration"/> or
/// <see cref="Abandon"/>. Transitions are logged at Warning (a misconfiguration at Error, once per open window, naming the condition only);
/// denied calls at Information at most once per window, so an outage does not flood the log.
/// </summary>
public sealed class LlmCircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly ILogger _logger;
    private int _consecutiveFailures;
    private DateTime? _openUntil;
    private bool _probeInFlight;
    private DateTime? _openDenialLoggedFor;
    private DateTime? _probeDenialLoggedFor;

    public LlmCircuitBreaker(int failureThreshold, TimeSpan cooldown, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cooldown, TimeSpan.Zero);
        _failureThreshold = failureThreshold;
        _cooldown = cooldown;
        _logger = logger ?? NullLogger.Instance;
    }

    public BreakerState State(DateTime now)
    {
        lock (_gate)
        {
            return _openUntil is not { } until ? BreakerState.Closed : now < until ? BreakerState.Open : BreakerState.HalfOpen;
        }
    }

    public BreakerTicket TryEnter(DateTime now)
    {
        lock (_gate)
        {
            if (_openUntil is not { } until)
            {
                return new BreakerTicket(true, false, BreakerDenial.None, null);
            }

            if (now < until)
            {
                if (_openDenialLoggedFor != until)
                {
                    _openDenialLoggedFor = until;
                    _logger.LogInformation(
                        "LLM calls are denied by the open circuit breaker until {ReopensAt:o}; those conversations use the rules meanwhile. Further denials in this window are not logged.",
                        until);
                }

                return new BreakerTicket(false, false, BreakerDenial.Open, until);
            }

            if (_probeInFlight)
            {
                if (_probeDenialLoggedFor != until)
                {
                    _probeDenialLoggedFor = until;
                    _logger.LogInformation(
                        "LLM calls are deferred while the circuit breaker's probe is in flight; those conversations use the rules and are read again shortly. Further deferrals in this window are not logged.");
                }

                return new BreakerTicket(false, false, BreakerDenial.ProbeInFlight, null);
            }

            _probeInFlight = true;
            _logger.LogWarning("LLM circuit breaker half-open after a cooldown of {CooldownSeconds} s: one probe call is admitted.", _cooldown.TotalSeconds);
            return new BreakerTicket(true, true, BreakerDenial.None, null);
        }
    }

    /// <summary>A usable answer: the breaker closes and the count starts over.</summary>
    public void RecordSuccess(BreakerTicket ticket)
    {
        if (!ticket.Admitted)
        {
            return;
        }

        lock (_gate)
        {
            var wasOpen = _openUntil is not null;
            _consecutiveFailures = 0;
            _openUntil = null;
            if (ticket.IsProbe)
            {
                _probeInFlight = false;
            }

            if (wasOpen)
            {
                _logger.LogWarning("LLM circuit breaker closed: a call got a usable answer.");
            }
        }
    }

    /// <summary>The call timed out, was rate limited or the provider was unavailable. <paramref name="now"/> is the time the call ended.</summary>
    public void RecordTransportFailure(BreakerTicket ticket, DateTime now)
    {
        if (!ticket.Admitted)
        {
            return;
        }

        lock (_gate)
        {
            if (ticket.IsProbe)
            {
                _probeInFlight = false;
                Open(now, "the probe call failed");
                return;
            }

            if (++_consecutiveFailures >= _failureThreshold)
            {
                Open(now, $"{_consecutiveFailures} consecutive transport failures");
            }
        }
    }

    /// <summary>
    /// The provider refused the credentials, the permission or the model (<paramref name="condition"/>: <c>authentication</c>,
    /// <c>permission</c>, <c>model_not_found</c>): it opens at once, with one Error line per open window naming the condition, never the key.
    /// </summary>
    public void RecordMisconfiguration(BreakerTicket ticket, DateTime now, string condition)
    {
        if (!ticket.Admitted)
        {
            return;
        }

        lock (_gate)
        {
            if (ticket.IsProbe)
            {
                _probeInFlight = false;
            }

            Open(now, "a misconfiguration: " + condition);
            _logger.LogError(
                "The LLM provider rejected the configuration ({Condition}): check Anthropic:ApiKey, its permissions and Classification:Llm:Model. No call is made until {ReopensAt:o}; the rules decide meanwhile.",
                condition, _openUntil);
        }
    }

    /// <summary>The call ended neither in a usable answer nor in a transport failure: nothing is counted or reset; only the probe slot is freed.</summary>
    public void Abandon(BreakerTicket ticket)
    {
        if (!ticket.IsProbe)
        {
            return;
        }

        lock (_gate)
        {
            _probeInFlight = false;
        }
    }

    private void Open(DateTime now, string cause)
    {
        _openUntil = now + _cooldown;
        _logger.LogWarning(
            "LLM circuit breaker opened after {Cause} (threshold {Threshold}); no call for {CooldownSeconds} s, until {ReopensAt:o}. The rules decide meanwhile.",
            cause, _failureThreshold, _cooldown.TotalSeconds, _openUntil);
        _consecutiveFailures = 0;
    }
}
