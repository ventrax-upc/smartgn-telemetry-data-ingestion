using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>
/// Read model used by the private gRPC contract: bounds the requested
/// identifiers and page size before hitting the time-series repository.
/// </summary>
public sealed class TelemetryQueryService
{
    private readonly ITelemetryStore _store;
    private readonly IDeviceAssociationStore? _associations;

    public TelemetryQueryService(ITelemetryStore store, IDeviceAssociationStore? associations = null)
    {
        _store = store;
        _associations = associations;
    }

    /// <summary>Readings of the given devices between two instants, newest first.</summary>
    public Task<IReadOnlyList<TelemetryReading>> GetBatchAsync(
        IReadOnlyCollection<string> deviceIds,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default) =>
        _store.GetBatchAsync(ValidDevices(deviceIds), from, to, NormalizeLimit(limit), cancellationToken);

    /// <summary>Latest reading of each requested device.</summary>
    public Task<IReadOnlyList<TelemetryReading>> GetLatestAsync(
        IReadOnlyCollection<string> deviceIds,
        int limit,
        CancellationToken cancellationToken = default) =>
        _store.GetLatestAsync(ValidDevices(deviceIds), NormalizeLimit(limit), cancellationToken);

    public async Task<IReadOnlyList<TelemetryReading>> GetBatchBySupplyPointsAsync(
        IReadOnlyCollection<string> pointIds,
        string accountId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var points = ValidDevices(pointIds);
        if (_associations is null || points.Length == 0 || from >= to)
        {
            return [];
        }

        var associations = await _associations.FindByPointIdsAsync(points, cancellationToken);
        var devices = associations.Select(association => association.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var readings = await _store.GetBatchAsync(
            devices, from, to, NormalizeLimit(limit), cancellationToken);
        return readings.Select(reading =>
                associations.FirstOrDefault(association =>
                    association.DeviceId == reading.DeviceId &&
                    association.Version == reading.AssociationVersion &&
                    association.AccountId == accountId &&
                    points.Contains(association.PointId, StringComparer.Ordinal) &&
                    association.ValidFrom <= reading.MeasuredAt &&
                    (association.ValidTo is null || reading.MeasuredAt < association.ValidTo)))
            .Select((association, index) => (association, index))
            .Where(item => item.association is not null)
            .Select(item => readings[item.index] with { SupplyPointId = item.association!.PointId })
            .ToArray();
    }

    public async Task<IReadOnlyList<TelemetryReading>> GetLatestBySupplyPointsAsync(
        IReadOnlyCollection<string> pointIds,
        string accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var points = ValidDevices(pointIds);
        if (_associations is null || points.Length == 0)
        {
            return [];
        }

        var associations = (await _associations.FindByPointIdsAsync(points, cancellationToken))
            .Where(association => association.ValidTo is null && association.AccountId == accountId)
            .ToArray();
        var readings = await _store.GetLatestAsync(
            associations.Select(association => association.DeviceId).Distinct(StringComparer.Ordinal).ToArray(),
            NormalizeLimit(limit),
            cancellationToken);
        return readings.Select(reading =>
                associations.FirstOrDefault(association =>
                    association.DeviceId == reading.DeviceId && association.Version == reading.AssociationVersion))
            .Select((association, index) => (association, index))
            .Where(item => item.association is not null)
            .Select(item => readings[item.index] with { SupplyPointId = item.association!.PointId })
            .ToArray();
    }

    /// <summary>Drop identifiers that could poison the Flux query.</summary>
    public static string[] ValidDevices(IReadOnlyCollection<string> deviceIds) =>
        deviceIds
            .Where(device => TelemetryRules.IsValidIdentifier(device))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static int NormalizeLimit(int limit) =>
        limit <= 0
            ? TelemetryRules.DefaultQueryLimit
            : Math.Min(limit, TelemetryRules.MaxQueryLimit);
}
