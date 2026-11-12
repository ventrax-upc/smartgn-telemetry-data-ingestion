namespace Smartgn.Telemetry.Configuration;

/// <summary>MQTT 5 broker settings used by the telemetry consumer.</summary>
public sealed record MqttSettings
{
    /// <summary>Broker URL, for example <c>tcp://localhost:1883</c> or <c>ssl://broker:8883</c>.</summary>
    public string Url { get; init; } = "tcp://localhost:1883";

    public string ClientId { get; init; } = "smartgn-telemetry";

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Topic filter with the device wildcard.</summary>
    public string Topic { get; init; } = "smartgn/devices/+/telemetry";
}

/// <summary>InfluxDB settings for the time-series repository.</summary>
public sealed record InfluxSettings
{
    public string Url { get; init; } = "http://localhost:18086";

    public string Token { get; init; } = string.Empty;

    public string Org { get; init; } = "smartgn";

    public string Bucket { get; init; } = "telemetry";

    public string Measurement { get; init; } = "telemetry";

    public string AssociationMeasurement { get; init; } = "device_associations";
}

/// <summary>Endpoints and credentials shared with sibling services.</summary>
public sealed record IntegrationSettings
{
    /// <summary>Base address of Installation &amp; Device Management.</summary>
    public string InstallationBaseUrl { get; init; } = "http://localhost:8081";

    /// <summary>Full URL of the Alerting &amp; Notifications event sink.</summary>
    public string AlertingEventsSinkUrl { get; init; } = "http://localhost:8086/internal/v1/telemetry-events";

    /// <summary>Service-to-service bearer token; also protects the private gRPC contract.</summary>
    public string? InternalServiceToken { get; init; }
}

/// <summary>Tunable thresholds of this service.</summary>
public sealed record TelemetrySettings
{
    /// <summary>HTTP port of the health endpoints.</summary>
    public int HttpPort { get; init; } = 8085;

    /// <summary>gRPC port of the private query contract.</summary>
    public int GrpcPort { get; init; } = 9084;
}
