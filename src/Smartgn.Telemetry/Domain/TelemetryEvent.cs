namespace Smartgn.Telemetry.Domain;

/// <summary>
/// Normalized telemetry event as published by a provisioned device on the
/// <c>smartgn/devices/{deviceId}/telemetry</c> MQTT topic (QoS 1).
/// </summary>
public sealed record TelemetryEvent(
    int SchemaVersion,
    string DeviceId,
    string EventId,
    long SequenceNumber,
    DateTimeOffset MeasuredAt,
    long AssociationVersion,
    IReadOnlyDictionary<string, double> Metrics);
