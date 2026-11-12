using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests.Fakes;

/// <summary>
/// In-memory replacement of the InfluxDB repository: same semantics for
/// deduplication, ordering and limits, no external dependency.
/// </summary>
public sealed class InMemoryTelemetryStore : ITelemetryStore
{
    private readonly Dictionary<(string DeviceId, string EventId), TelemetryReading> _readings = new();

    /// <summary>When true the next SaveAsync throws, simulating an outage.</summary>
    public bool FailNextSave { get; set; }

    /// <summary>When true the next read query throws, simulating an outage.</summary>
    public bool FailNextRead { get; set; }

    public bool IsHealthy { get; set; } = true;

    public IReadOnlyCollection<TelemetryReading> All => _readings.Values;

    public Task<bool> ContainsAsync(string deviceId, string eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_readings.ContainsKey((deviceId, eventId)));

    public Task SaveAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new InvalidOperationException("store_unavailable");
        }

        _readings[(reading.DeviceId, reading.EventId)] = reading;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelemetryReading>> GetBatchAsync(
        IReadOnlyCollection<string> deviceIds,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNextReadFails();
        var result = _readings.Values
            .Where(reading => deviceIds.Contains(reading.DeviceId))
            .Where(reading => reading.MeasuredAt >= from && reading.MeasuredAt <= to)
            .OrderByDescending(reading => reading.MeasuredAt)
            .Take(limit)
            .ToArray();

        return Task.FromResult<IReadOnlyList<TelemetryReading>>(result);
    }

    public Task<IReadOnlyList<TelemetryReading>> GetLatestAsync(
        IReadOnlyCollection<string> deviceIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNextReadFails();
        var result = deviceIds
            .Select(device => _readings.Values
                .Where(reading => reading.DeviceId == device)
                .OrderByDescending(reading => reading.MeasuredAt)
                .FirstOrDefault())
            .OfType<TelemetryReading>()
            .OrderByDescending(reading => reading.MeasuredAt)
            .Take(limit)
            .ToArray();

        return Task.FromResult<IReadOnlyList<TelemetryReading>>(result);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsHealthy);

    private void ThrowIfNextReadFails()
    {
        if (!FailNextRead)
        {
            return;
        }

        FailNextRead = false;
        throw new InvalidOperationException("store_unavailable");
    }
}
