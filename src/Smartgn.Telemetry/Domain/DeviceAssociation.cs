namespace Smartgn.Telemetry.Domain;

/// <summary>One confirmed device-to-supply-point association version.</summary>
public sealed record DeviceAssociation(
    string DeviceId,
    long Version,
    string AccountId,
    string PropertyId,
    string PointId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

public enum DeviceConnectionState
{
    Disconnected,
    Connected,
}

public sealed record OperationalDeviceStatus(
    string DeviceId,
    DeviceConnectionState ConnectionState,
    DateTimeOffset? LastContact);

public sealed record DeviceAssociationConfirmation(string DeviceId, long Version, bool Confirmed);

public sealed record BufferDrainResult(bool Drained, long PendingReadings, string EvidenceId);

/// <summary>A private device contract did not satisfy its documented shape.</summary>
public sealed class DeviceContractException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
