using System.Net;
using Smartgn.Telemetry.Application;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// Asks Installation &amp; Device Management whether a device is currently
/// provisioned and associated:
/// GET /internal/v1/devices/{deviceId}/authorization.
/// Any unavailable dependency is treated conservatively — the reading is
/// rejected instead of fabricating an authorization ("unknown connectivity is
/// never a successful business operation").
/// </summary>
public sealed class InstallationAuthorizationClient(HttpClient httpClient) : IDeviceAuthorizationClient
{
    public async Task<DeviceAuthorizationResult> AuthorizeAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"/internal/v1/devices/{Uri.EscapeDataString(deviceId)}/authorization",
                cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new DeviceAuthorizationResult(true, null),
                HttpStatusCode.Forbidden => new DeviceAuthorizationResult(false, "device_forbidden"),
                HttpStatusCode.NotFound => new DeviceAuthorizationResult(false, "device_unknown"),
                HttpStatusCode.Unauthorized => new DeviceAuthorizationResult(false, "service_token_rejected"),
                _ => new DeviceAuthorizationResult(false, "installation_unavailable"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeviceAuthorizationResult(false, "installation_timeout");
        }
        catch (HttpRequestException)
        {
            return new DeviceAuthorizationResult(false, "installation_unavailable");
        }
    }
}
