using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

public enum CircuitState
{
    Closed,
    Open,
    /// <summary>The probe interval elapsed; one attempt is allowed through.</summary>
    HalfOpen,
}

/// <summary>
/// Consecutive-failure circuit used before calling Alerting &amp;
/// Notifications (QAS18: timeout 2 s, open after 5 consecutive failures,
/// probe again after 30 s). Thread-safe; the gate is held only for state
/// transitions, never during I/O.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _probeInterval;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;
    private bool _probeInFlight;

    public CircuitBreaker(
        int failureThreshold = TelemetryRules.CircuitFailureThreshold,
        TimeSpan? probeInterval = null,
        TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        _failureThreshold = failureThreshold;
        _probeInterval = probeInterval ?? TelemetryRules.CircuitProbeInterval;
        _clock = clock ?? TimeProvider.System;
    }

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return StateLocked(_clock.GetUtcNow());
            }
        }
    }

    /// <summary>True when a call may be attempted right now.</summary>
    public bool CanAttempt()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            switch (StateLocked(now))
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.Open when now - _openedAt!.Value >= _probeInterval:
                    // Half-open: a single probe may pass.
                    _probeInFlight = true;
                    return true;
                case CircuitState.HalfOpen:
                    if (!_probeInFlight)
                    {
                        _probeInFlight = true;
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
            _probeInFlight = false;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            var wasHalfOpen = StateLocked(_clock.GetUtcNow()) == CircuitState.HalfOpen || _probeInFlight;
            _probeInFlight = false;

            if (wasHalfOpen)
            {
                // A failed probe reopens the circuit and restarts the interval.
                _openedAt = _clock.GetUtcNow();
                _consecutiveFailures = _failureThreshold;
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _openedAt = _clock.GetUtcNow();
            }
        }
    }

    private CircuitState StateLocked(DateTimeOffset now)
    {
        if (_openedAt is null)
        {
            return CircuitState.Closed;
        }

        if (now - _openedAt.Value >= _probeInterval)
        {
            return CircuitState.HalfOpen;
        }

        return CircuitState.Open;
    }
}
