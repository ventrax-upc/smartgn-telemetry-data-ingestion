namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Status of one requested device. <c>LastContact</c> is when the reading was
/// received here; it is <c>null</c> when the device has never been heard from,
/// never an assumed observation (RNF17).
/// </summary>
public sealed record OperationalStatusView(string DeviceId, string ConnectionState, DateTimeOffset? LastContact);
