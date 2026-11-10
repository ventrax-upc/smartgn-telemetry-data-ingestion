using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>
/// Read model used by the private gRPC contract: bounds the requested
/// identifiers and page size before hitting the time-series repository.
/// </summary>
public sealed class TelemetryQueryService(ITelemetryStore store)
{
    /// <summary>Readings of the given devices between two instants, newest first.</summary>
    public Task<IReadOnlyList<TelemetryReading>> GetBatchAsync(
        IReadOnlyCollection<string> deviceIds,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default) =>
        store.GetBatchAsync(ValidDevices(deviceIds), from, to, NormalizeLimit(limit), cancellationToken);

    /// <summary>Latest reading of each requested device.</summary>
    public Task<IReadOnlyList<TelemetryReading>> GetLatestAsync(
        IReadOnlyCollection<string> deviceIds,
        int limit,
        CancellationToken cancellationToken = default) =>
        store.GetLatestAsync(ValidDevices(deviceIds), NormalizeLimit(limit), cancellationToken);

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
