using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests.Fakes;

/// <summary>
/// In-memory replacement of the InfluxDB association ledger: same
/// version-keyed semantics, no external dependency.
/// </summary>
public sealed class InMemoryDeviceAssociationStore : IDeviceAssociationStore
{
    private readonly Dictionary<(string DeviceId, long Version), DeviceAssociation> _ledger = new();

    /// <summary>When true every call throws, simulating a ledger outage.</summary>
    public bool Fail { get; set; }

    /// <summary>Number of times a version was written, to prove idempotency.</summary>
    public int SaveCalls { get; private set; }

    public IReadOnlyCollection<DeviceAssociation> All => _ledger.Values;

    public Task SaveAsync(DeviceAssociation association, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(association);
        if (Fail)
        {
            throw new InvalidOperationException("ledger_unavailable");
        }

        SaveCalls++;
        _ledger[(association.DeviceId, association.Version)] = association;
        return Task.CompletedTask;
    }

    public Task<DeviceAssociation?> FindAsync(
        string deviceId,
        long version,
        CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("ledger_unavailable");
        }

        _ledger.TryGetValue((deviceId, version), out var association);
        return Task.FromResult(association);
    }

    public Task<IReadOnlyList<DeviceAssociation>> FindByPointIdsAsync(
        IReadOnlyCollection<string> pointIds,
        CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("ledger_unavailable");
        }

        IReadOnlyList<DeviceAssociation> associations = _ledger.Values
            .Where(association => pointIds.Contains(association.PointId, StringComparer.Ordinal))
            .ToArray();
        return Task.FromResult(associations);
    }
}
