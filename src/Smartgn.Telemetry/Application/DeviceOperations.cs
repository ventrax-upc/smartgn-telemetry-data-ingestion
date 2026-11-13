using Microsoft.Extensions.Logging;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>Use cases for private device-lifecycle contracts.</summary>
public sealed class DeviceOperations(
    IDeviceAssociationStore associations,
    ITelemetryStore telemetry,
    TelemetryEventBuffer outbox,
    TimeProvider clock,
    ILogger<DeviceOperations> logger)
{
    public async Task<DeviceAssociationConfirmation> SynchronizeAssociationAsync(
        DeviceAssociation association,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(association);
        ValidateAssociation(association);

        try
        {
            await associations.SaveAsync(association, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not persist association {DeviceId} version {Version}",
                association.DeviceId, association.Version);
            throw new TelemetryStoreUnavailableException("The device association ledger is unavailable.", exception);
        }

        return new DeviceAssociationConfirmation(association.DeviceId, association.Version, Confirmed: true);
    }

    public async Task<IReadOnlyList<OperationalDeviceStatus>> QueryOperationalStatusAsync(
        string accountId,
        IReadOnlyCollection<string> deviceIds,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        if (!TelemetryRules.IsValidIdentifier(accountId))
        {
            throw new DeviceContractException("accountId_invalid", "accountId is invalid.");
        }

        if (!string.Equals(purpose, TelemetryRules.DeviceOperationsPurpose, StringComparison.Ordinal))
        {
            throw new DeviceContractException("purpose_invalid", "purpose must be DEVICE_OPERATIONS.");
        }

        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count > TelemetryRules.MaxQueryLimit)
        {
            throw new DeviceContractException("deviceIds_limit", "deviceIds exceeds the maximum query size.");
        }

        if (deviceIds.Any(deviceId => !TelemetryRules.IsValidIdentifier(deviceId)))
        {
            throw new DeviceContractException("deviceId_invalid", "A device identifier is invalid.");
        }

        if (deviceIds.Count == 0)
        {
            return [];
        }

        IReadOnlyList<TelemetryReading> readings;
        try
        {
            readings = await telemetry.GetLatestAsync(
                deviceIds.Distinct(StringComparer.Ordinal).ToArray(),
                deviceIds.Count,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read latest telemetry for an operational-status query");
            throw new TelemetryStoreUnavailableException("The telemetry repository is unavailable.", exception);
        }

        var latestByDevice = readings
            .GroupBy(reading => reading.DeviceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(reading => reading.ReceivedAt).First(),
                StringComparer.Ordinal);
        var now = clock.GetUtcNow();

        return deviceIds
            .Select(deviceId =>
            {
                if (!latestByDevice.TryGetValue(deviceId, out var reading))
                {
                    return new OperationalDeviceStatus(
                        deviceId,
                        DeviceConnectionState.Disconnected,
                        LastContact: null);
                }

                var age = now - reading.ReceivedAt;
                var connected = age <= TelemetryRules.StalenessThreshold && age >= -TelemetryRules.FutureTolerance;
                return new OperationalDeviceStatus(
                    deviceId,
                    connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected,
                    reading.ReceivedAt);
            })
            .ToArray();
    }

    public async Task<BufferDrainResult> DrainBufferAsync(
        string deviceId,
        long associationVersion,
        CancellationToken cancellationToken = default)
    {
        if (!TelemetryRules.IsValidIdentifier(deviceId))
        {
            throw new DeviceContractException("deviceId_invalid", "deviceId is invalid.");
        }

        if (associationVersion < 0)
        {
            throw new DeviceContractException("association_version_invalid", "associationVersion cannot be negative.");
        }

        DeviceAssociation? association;
        try
        {
            association = await associations.FindAsync(deviceId, associationVersion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read association {DeviceId} version {Version}",
                deviceId, associationVersion);
            throw new TelemetryStoreUnavailableException("The device association ledger is unavailable.", exception);
        }

        if (association is null)
        {
            return new BufferDrainResult(Drained: false, PendingReadings: 0, EvidenceId: string.Empty);
        }

        var pending = outbox.CountPending(deviceId, associationVersion, clock.GetUtcNow());
        if (pending > 0)
        {
            return new BufferDrainResult(Drained: false, PendingReadings: pending, EvidenceId: string.Empty);
        }

        return new BufferDrainResult(
            Drained: true,
            PendingReadings: 0,
            EvidenceId: $"{deviceId}:{associationVersion}:{Guid.NewGuid():N}");
    }

    private static void ValidateAssociation(DeviceAssociation association)
    {
        if (!TelemetryRules.IsValidIdentifier(association.DeviceId))
        {
            throw new DeviceContractException("deviceId_invalid", "deviceId is invalid.");
        }

        if (association.Version < 0)
        {
            throw new DeviceContractException("version_invalid", "version cannot be negative.");
        }

        if (!TelemetryRules.IsValidIdentifier(association.AccountId))
        {
            throw new DeviceContractException("accountId_invalid", "accountId is invalid.");
        }

        if (!TelemetryRules.IsValidIdentifier(association.PropertyId))
        {
            throw new DeviceContractException("propertyId_invalid", "propertyId is invalid.");
        }

        if (!TelemetryRules.IsValidIdentifier(association.PointId))
        {
            throw new DeviceContractException("pointId_invalid", "pointId is invalid.");
        }

        if (association.ValidTo is { } validTo && validTo < association.ValidFrom)
        {
            throw new DeviceContractException("validity_invalid", "validTo must not precede validFrom.");
        }
    }
}
