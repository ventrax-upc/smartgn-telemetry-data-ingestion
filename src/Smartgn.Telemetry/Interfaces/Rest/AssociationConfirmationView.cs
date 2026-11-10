namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Acknowledgement of an association. Installation only accepts a confirmation
/// that names the exact device and version it sent.
/// </summary>
public sealed record AssociationConfirmationView(string? DeviceId, long Version, bool Confirmed);
