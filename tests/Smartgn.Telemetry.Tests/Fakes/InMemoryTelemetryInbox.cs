using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests.Fakes;

public sealed class InMemoryTelemetryInbox : ITelemetryInbox
{
    public List<TelemetryReading> Readings { get; } = [];
    public bool Full { get; set; }

    public Task<int> CountPendingAsync(string deviceId, long associationVersion, CancellationToken cancellationToken) =>
        Task.FromResult(Readings.Count(reading =>
            reading.DeviceId == deviceId && reading.AssociationVersion == associationVersion));

    public Task<IngestionOutcome> EnqueueAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        var existing = Readings.Find(item => item.DeviceId == reading.DeviceId && item.EventId == reading.EventId);
        if (existing is not null)
        {
            return Task.FromResult(existing.SequenceNumber == reading.SequenceNumber
                ? IngestionOutcome.Duplicate
                : IngestionOutcome.Conflict);
        }

        if (Full)
        {
            return Task.FromResult(IngestionOutcome.StoreUnavailable);
        }

        Readings.Add(reading);
        return Task.FromResult(IngestionOutcome.Accepted);
    }
}
