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
        long associationVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"/internal/v1/devices/{Uri.EscapeDataString(deviceId)}/authorization?associationVersion={associationVersion}",
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Forbidden)
            {
                return new DeviceAuthorizationResult(false, "device_forbidden");
            }

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return new DeviceAuthorizationResult(false, "device_unknown");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                return new DeviceAuthorizationResult(false, "service_token_rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DeviceAuthorizationResult(false, "installation_unavailable");
            }

            var authorization = await response.Content.ReadFromJsonAsync<AuthorizationResponse>(cancellationToken);
            if (authorization is null ||
                !string.Equals(authorization.DeviceId, deviceId, StringComparison.Ordinal) ||
                authorization.AssociationVersion != associationVersion)
            {
                return new DeviceAuthorizationResult(false, "installation_contract_invalid");
            }

            return authorization.Authorized
                ? new DeviceAuthorizationResult(true, null)
                : new DeviceAuthorizationResult(false, "device_forbidden");
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

    private sealed record AuthorizationResponse(string DeviceId, long AssociationVersion, bool Authorized);
}
