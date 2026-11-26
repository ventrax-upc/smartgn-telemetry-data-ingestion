using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

public enum IngestionOutcome
{
    Accepted,
    Duplicate,
    Conflict,
    Unauthorized,
    InvalidPayload,
    InvalidTimestamp,
    StoreUnavailable,
}

public sealed record IngestionResult(IngestionOutcome Outcome, TelemetryReading? Reading, string? Error);

public interface ITelemetryInbox
{
    Task<int> CountPendingAsync(string deviceId, long associationVersion, CancellationToken cancellationToken);

    Task<IngestionOutcome> EnqueueAsync(TelemetryReading reading, CancellationToken cancellationToken);
}

public sealed class TelemetryIngestionService(
    ITelemetryInbox inbox,
    IDeviceAuthorizationClient authorization,
    TimeProvider clock)
{
    public async Task<IngestionResult> IngestAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var measuredAt = telemetryEvent.MeasuredAt.ToUniversalTime();
        if (TelemetryRules.IsFromTheFuture(measuredAt, now) ||
            measuredAt < DateTimeOffset.UnixEpoch)
        {
            return new(IngestionOutcome.InvalidTimestamp, null, "measured_at_out_of_range");
        }

        if (!TelemetryRules.IsValidIdentifier(telemetryEvent.DeviceId, 64) ||
            !TelemetryRules.IsValidIdentifier(telemetryEvent.EventId, 128))
        {
            return new(IngestionOutcome.InvalidPayload, null, "identifier_invalid");
        }

        var authorizationResult = await authorization.AuthorizeAsync(
            telemetryEvent.DeviceId, telemetryEvent.AssociationVersion, cancellationToken);
        if (!authorizationResult.IsAuthorized)
        {
            var outcome = authorizationResult.IsTransient
                ? IngestionOutcome.StoreUnavailable
                : IngestionOutcome.Unauthorized;
            return new(outcome, null, authorizationResult.Reason);
        }

        var reading = new TelemetryReading(
            telemetryEvent.DeviceId, telemetryEvent.EventId, telemetryEvent.SequenceNumber,
            measuredAt, now, telemetryEvent.Metrics,
            TelemetryRules.EvaluateFreshness(measuredAt, now), telemetryEvent.AssociationVersion);

        var result = await inbox.EnqueueAsync(reading, cancellationToken);
        return new(result, result == IngestionOutcome.Accepted ? reading : null, result switch
        {
            IngestionOutcome.Conflict => "event_id_payload_conflict",
            IngestionOutcome.StoreUnavailable => "pending_capacity_exhausted",
            _ => null,
        });
    }
}
