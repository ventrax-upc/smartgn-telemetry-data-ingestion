using Smartgn.Telemetry.Application;

namespace Smartgn.Telemetry.Tests.Fakes;

/// <summary>Installation authorization port with a fixed set of allowed devices.</summary>
public sealed class StubDeviceAuthorizationClient : IDeviceAuthorizationClient
{
    private readonly HashSet<string> _authorized;

    public StubDeviceAuthorizationClient(params string[] authorizedDevices)
    {
        _authorized = new HashSet<string>(authorizedDevices, StringComparer.Ordinal);
    }

    /// <summary>Reason returned for every rejected device.</summary>
    public string RejectReason { get; set; } = "device_unknown";

    public int Calls { get; private set; }

    public long LastAssociationVersion { get; private set; }

    public Task<DeviceAuthorizationResult> AuthorizeAsync(
        string deviceId,
        long associationVersion,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastAssociationVersion = associationVersion;
        var authorized = _authorized.Contains(deviceId);
        return Task.FromResult(new DeviceAuthorizationResult(authorized, authorized ? null : RejectReason));
    }
}
