using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class CircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static (CircuitBreaker Breaker, FixedTimeProvider Clock) Create(
        int failureThreshold = 5,
        TimeSpan? probeInterval = null)
    {
        var clock = new FixedTimeProvider(Start);
        var breaker = new CircuitBreaker(failureThreshold, probeInterval, clock);
        return (breaker, clock);
    }

    [Fact]
    public void NewCircuit_IsClosedAndAllowsCalls()
    {
        var (breaker, _) = Create();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.CanAttempt());
    }

    [Fact]
    public void FailuresBelowThreshold_KeepCircuitClosed()
    {
        var (breaker, _) = Create(failureThreshold: 5);

        for (var i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.CanAttempt());
    }

    [Fact]
    public void ReachingThreshold_OpensCircuitAndBlocksCalls()
    {
        var (breaker, _) = Create(failureThreshold: 5);

        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.CanAttempt());
    }

    [Fact]
    public void SuccessResetsTheFailureCounter()
    {
        var (breaker, _) = Create(failureThreshold: 5);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        for (var i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void ProbeIntervalElapsed_AllowsASingleProbe()
    {
        var (breaker, clock) = Create(probeInterval: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(breaker.CanAttempt());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.CanAttempt());
        // Only one probe may pass through while it is in flight.
        Assert.False(breaker.CanAttempt());
    }

    [Fact]
    public void SuccessfulProbe_ClosesTheCircuit()
    {
        var (breaker, clock) = Create(probeInterval: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(breaker.CanAttempt());
        breaker.RecordSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.CanAttempt());
    }

    [Fact]
    public void FailedProbe_ReopensAndRestartsTheInterval()
    {
        var (breaker, clock) = Create(probeInterval: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(breaker.CanAttempt());
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(breaker.CanAttempt());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(breaker.CanAttempt());
    }
}
