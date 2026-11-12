using Microsoft.Extensions.Logging;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>Result of processing one telemetry event.</summary>
public enum IngestionOutcome
{
    /// <summary>Reading stored and TelemetryProcessed announced.</summary>
    Accepted,

    /// <summary>eventId already stored for this device (QoS 1 redelivery).</summary>
    Duplicate,

    /// <summary>Installation does not authorize the device to publish.</summary>
    Unauthorized,

    /// <summary>The payload does not satisfy the schema contract.</summary>
    InvalidPayload,

    /// <summary>measuredAt is implausibly in the future.</summary>
    InvalidTimestamp,

    /// <summary>The time-series repository rejected or failed the write.</summary>
    StoreUnavailable,
}

public sealed record IngestionResult(IngestionOutcome Outcome, TelemetryReading? Reading, string? Error);

/// <summary>
/// Validation, authorization, deduplication and persistence of incoming
/// telemetry, plus the durable announcement of TelemetryProcessed.
/// </summary>
public sealed class TelemetryIngestionService(
    ITelemetryStore store,
    IDeviceAuthorizationClient authorization,
    ITelemetryEventPublisher publisher,
    TimeProvider clock,
    ILogger<TelemetryIngestionService> logger)
{
    public async Task<IngestionResult> IngestAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        if (TelemetryRules.IsFromTheFuture(telemetryEvent.MeasuredAt, now))
        {
            return new(IngestionOutcome.InvalidTimestamp, null, "measured_at_in_future");
        }

        if (!TelemetryRules.IsValidIdentifier(telemetryEvent.DeviceId, 64) ||
            !TelemetryRules.IsValidIdentifier(telemetryEvent.EventId, 128))
        {
            return new(IngestionOutcome.InvalidPayload, null, "identifier_invalid");
        }

        // An unknown or unprovisioned device never reaches the repository.
        var authorizationResult = await authorization.AuthorizeAsync(
            telemetryEvent.DeviceId,
            telemetryEvent.AssociationVersion,
            cancellationToken);
        if (!authorizationResult.IsAuthorized)
        {
            return new(IngestionOutcome.Unauthorized, null, authorizationResult.Reason ?? "unauthorized");
        }

        // At-least-once delivery: retransmissions are acknowledged, not stored twice.
        if (await store.ContainsAsync(telemetryEvent.DeviceId, telemetryEvent.EventId, cancellationToken))
        {
            return new(IngestionOutcome.Duplicate, null, "duplicate_event_id");
        }

        var reading = new TelemetryReading(
            telemetryEvent.DeviceId,
            telemetryEvent.EventId,
            telemetryEvent.SequenceNumber,
            telemetryEvent.MeasuredAt,
            now,
            telemetryEvent.Metrics,
            TelemetryRules.EvaluateFreshness(telemetryEvent.MeasuredAt, now),
            telemetryEvent.AssociationVersion);

        try
        {
            await store.SaveAsync(reading, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Telemetry store write failed for device {DeviceId} event {EventId}",
                reading.DeviceId, reading.EventId);
            return new(IngestionOutcome.StoreUnavailable, null, "store_unavailable");
        }

        try
        {
            await publisher.PublishAsync(
                new TelemetryProcessedEvent(
                    reading.DeviceId,
                    reading.EventId,
                    reading.SequenceNumber,
                    reading.MeasuredAt,
                    reading.ReceivedAt,
                    reading.AssociationVersion,
                    reading.Metrics),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The reading is durable; the local outbox will retry the event.
            logger.LogError(exception, "TelemetryProcessed enqueue failed for device {DeviceId} event {EventId}",
                reading.DeviceId, reading.EventId);
        }

        return new(IngestionOutcome.Accepted, reading, null);
    }
}
