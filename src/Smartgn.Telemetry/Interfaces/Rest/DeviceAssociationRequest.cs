using System.Text.Json;

namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Association synchronized by Installation. The timestamps are bound as raw
/// JSON because a sibling serializer may write an instant as ISO-8601 text or
/// as an epoch array; the controller normalizes them through the domain
/// parser instead of failing on a shape this service does not control.
/// </summary>
public sealed record DeviceAssociationRequest(
    string? DeviceId,
    long Version,
    string? AccountId,
    string? PropertyId,
    string? PointId,
    JsonElement? ValidFrom,
    JsonElement? ValidTo);
