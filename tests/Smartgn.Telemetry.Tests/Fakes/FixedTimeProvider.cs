namespace Smartgn.Telemetry.Tests.Fakes;

/// <summary>Deterministic clock so freshness and circuit tests are stable.</summary>
public sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
