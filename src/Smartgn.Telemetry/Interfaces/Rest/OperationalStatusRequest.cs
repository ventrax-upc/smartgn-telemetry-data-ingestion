namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Request for the operational status of a set of devices. The purpose must
/// name the decision being taken, so this port cannot be reused for another.
/// </summary>
public sealed record OperationalStatusRequest(
    string? AccountId,
    IReadOnlyCollection<string>? DeviceIds,
    string? Purpose);
