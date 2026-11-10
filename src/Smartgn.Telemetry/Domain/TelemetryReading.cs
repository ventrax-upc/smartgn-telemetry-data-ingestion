namespace Smartgn.Telemetry.Domain;

/// <summary>Reading freshness derived from the 30-second staleness rule.</summary>
public enum TelemetryFreshness
{
    /// <summary>measuredAt is within the allowed freshness window.</summary>
    Current,

    /// <summary>measuredAt is older than the staleness threshold.</summary>
    Delayed,
}

/// <summary>
/// A validated, normalized reading ready to be persisted in the time-series
/// repository and announced to Alerting & Notifications.
/// </summary>
public sealed record TelemetryReading(
    string DeviceId,
    string EventId,
    long SequenceNumber,
    DateTimeOffset MeasuredAt,
    DateTimeOffset ReceivedAt,
    IReadOnlyDictionary<string, double> Metrics,
    TelemetryFreshness Freshness,
    long AssociationVersion);
