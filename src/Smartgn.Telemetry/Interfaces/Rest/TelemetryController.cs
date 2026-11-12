using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Swashbuckle.AspNetCore.Annotations;

namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Private device contracts consumed by Installation &amp; Device Management
/// (report 4.3.2.5). Every action delegates to <see cref="DeviceOperations"/>;
/// this type only maps the wire shape onto the domain and translates a
/// rejected contract into a problem detail.
/// </summary>
[ApiController]
[Route("/internal/v1")]
public sealed class TelemetryController(DeviceOperations operations, ILogger<TelemetryController> logger)
    : ControllerBase
{
    /// <summary>
    /// Records a versioned device association and acknowledges the exact
    /// device and version that was sent.
    /// </summary>
    [HttpPost("device-associations")]
    [SwaggerOperation(
        Summary = "Synchronize a versioned device association",
        Description = "Confirms the device, account, property, supply point and association version provided by Installation. Requires the private service credential when configured.")]
    [ProducesResponseType(typeof(AssociationConfirmationView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AssociationConfirmationView>> SynchronizeAssociation(
        [FromBody] DeviceAssociationRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryTimestamp(request.ValidFrom, out var validFrom))
        {
            return ContractError("valid_from_invalid", "validFrom must be an ISO-8601 instant.");
        }

        DateTimeOffset? validTo = null;
        if (Present(request.ValidTo))
        {
            if (!TryTimestamp(request.ValidTo, out var parsed))
            {
                return ContractError("valid_to_invalid", "validTo must be an ISO-8601 instant.");
            }

            validTo = parsed;
        }

        try
        {
            var confirmation = await operations.SynchronizeAssociationAsync(
                new DeviceAssociation(
                    request.DeviceId!,
                    request.Version,
                    request.AccountId!,
                    request.PropertyId!,
                    request.PointId!,
                    validFrom,
                    validTo),
                cancellationToken);

            return Ok(new AssociationConfirmationView(
                confirmation.DeviceId,
                confirmation.Version,
                confirmation.Confirmed));
        }
        catch (DeviceContractException exception)
        {
            return ContractError(exception.Code, exception.Message);
        }
        catch (TelemetryStoreUnavailableException exception)
        {
            return RepositoryUnavailable(exception);
        }
    }

    /// <summary>
    /// Last contact of each requested device, restricted to the requested
    /// scope.
    /// </summary>
    [HttpPost("devices/status-query")]
    [SwaggerOperation(
        Summary = "Query device operational status",
        Description = "Returns last-contact state for the requested device identifiers within the account and purpose scope. Requires the private service credential when configured.")]
    [ProducesResponseType(typeof(IReadOnlyCollection<OperationalStatusView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyCollection<OperationalStatusView>>> QueryOperationalStatus(
        [FromBody] OperationalStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DeviceIds is null)
        {
            return ContractError("deviceIds_required", "deviceIds is required.");
        }

        try
        {
            var statuses = await operations.QueryOperationalStatusAsync(
                request.AccountId ?? string.Empty,
                request.DeviceIds,
                request.Purpose ?? string.Empty,
                cancellationToken);

            return Ok(statuses
                .Select(status => new OperationalStatusView(
                    status.DeviceId,
                    ConnectionState(status.ConnectionState),
                    status.LastContact))
                .ToArray());
        }
        catch (DeviceContractException exception)
        {
            return ContractError(exception.Code, exception.Message);
        }
        catch (TelemetryStoreUnavailableException exception)
        {
            return RepositoryUnavailable(exception);
        }
    }

    /// <summary>
    /// Confirms that nothing of the given association is still outstanding.
    /// </summary>
    [HttpPost("devices/{deviceId}/buffer-drain")]
    [SwaggerOperation(
        Summary = "Confirm an association buffer is drained",
        Description = "Reports whether readings remain outstanding for the specified device association version. Requires the private service credential when configured.")]
    [ProducesResponseType(typeof(BufferDrainView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<BufferDrainView>> DrainBuffer(
        string deviceId,
        [FromBody] BufferDrainRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await operations.DrainBufferAsync(deviceId, request.AssociationVersion, cancellationToken);
            return Ok(new BufferDrainView(result.Drained, result.PendingReadings, result.EvidenceId));
        }
        catch (DeviceContractException exception)
        {
            return ContractError(exception.Code, exception.Message);
        }
        catch (TelemetryStoreUnavailableException exception)
        {
            return RepositoryUnavailable(exception);
        }
    }

    private ObjectResult ContractError(string code, string message)
    {
        // The stable code belongs to logs and tests, never to the response.
        logger.LogWarning("Rejected internal contract {Code}: {Message}", code, message);
        return Problem(statusCode: StatusCodes.Status400BadRequest, detail: message);
    }

    private ObjectResult RepositoryUnavailable(Exception exception)
    {
        // An outage is a retryable unknown state, never a rejection of the
        // contract and never a stack trace.
        logger.LogWarning(exception, "Telemetry repository unavailable while serving an internal contract");
        return Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "The telemetry repository is unavailable; retry the operation.");
    }

    private static bool Present(JsonElement? element) =>
        element is { } value &&
        value.ValueKind is not System.Text.Json.JsonValueKind.Undefined and
            not System.Text.Json.JsonValueKind.Null;

    private static bool TryTimestamp(JsonElement? element, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return Present(element) && TelemetryRules.TryParseTimestamp(element!.Value, out timestamp);
    }

    private static string ConnectionState(DeviceConnectionState state) =>
        state == DeviceConnectionState.Connected ? "CONNECTED" : "DISCONNECTED";
}
