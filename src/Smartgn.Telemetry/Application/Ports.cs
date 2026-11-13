using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>Outbound ports of the telemetry bounded context.</summary>
public interface ITelemetryStore
{
    /// <summary>Deduplication check: does the event already exist? (eventId per device)</summary>
    Task<bool> ContainsAsync(string deviceId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Persists a normalized reading in the time-series repository.</summary>
    Task SaveAsync(TelemetryReading reading, CancellationToken cancellationToken = default);

    /// <summary>Readings of the given devices between two instants, newest first.</summary>
    Task<IReadOnlyList<TelemetryReading>> GetBatchAsync(
        IReadOnlyCollection<string> deviceIds,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Latest reading of each requested device.</summary>
    Task<IReadOnlyList<TelemetryReading>> GetLatestAsync(
        IReadOnlyCollection<string> deviceIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Readiness probe for /ready.</summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of asking Installation whether a device may publish.</summary>
public sealed record DeviceAuthorizationResult(bool IsAuthorized, string? Reason);

/// <summary>
/// Private contract with Installation &amp; Device Management:
/// GET /internal/v1/devices/{deviceId}/authorization.
/// </summary>
public interface IDeviceAuthorizationClient
{
    Task<DeviceAuthorizationResult> AuthorizeAsync(
        string deviceId,
        long associationVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Stores versioned device associations confirmed by Installation.</summary>
public interface IDeviceAssociationStore
{
    Task SaveAsync(DeviceAssociation association, CancellationToken cancellationToken = default);

    Task<DeviceAssociation?> FindAsync(
        string deviceId,
        long version,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceAssociation>> FindByPointIdsAsync(
        IReadOnlyCollection<string> pointIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers <c>TelemetryProcessed</c> events to Alerting &amp; Notifications.
/// The implementation buffers locally, so failures are retried at-least-once.
/// </summary>
public interface ITelemetryEventPublisher
{
    Task PublishAsync(TelemetryProcessedEvent telemetryEvent, CancellationToken cancellationToken = default);
}

/// <summary>Domain event announced after a reading is durably stored.</summary>
public sealed record TelemetryProcessedEvent(
    string DeviceId,
    string EventId,
    long SequenceNumber,
    DateTimeOffset MeasuredAt,
    DateTimeOffset ProcessedAt,
    long AssociationVersion,
    IReadOnlyDictionary<string, double> Metrics);
