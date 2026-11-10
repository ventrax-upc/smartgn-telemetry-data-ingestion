using Grpc.AspNetCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Configuration;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Grpc;
using Smartgn.Telemetry.Infrastructure;
using AppTelemetryQueryService = Smartgn.Telemetry.Application.TelemetryQueryService;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Environment variables keep the names used by the shared compositions of
// the sibling services; otherwise the JSON configuration wins.
string Resolve(string configKey, string? environmentVariable, string fallback)
{
    if (environmentVariable is not null &&
        Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment)
    {
        return fromEnvironment;
    }

    return configuration[configKey] ?? fallback;
}

string? ResolveOptional(string configKey, string? environmentVariable)
{
    var value = Resolve(configKey, environmentVariable, string.Empty);
    return value.Length > 0 ? value : null;
}

var telemetrySettings = new TelemetrySettings
{
    HttpPort = int.TryParse(Resolve("Telemetry:HttpPort", "PORT", "8084"), out var httpPort) ? httpPort : 8084,
    GrpcPort = int.TryParse(Resolve("Telemetry:GrpcPort", "GRPC_PORT", "9084"), out var grpcPort) ? grpcPort : 9084,
};

var mqttSettings = new MqttSettings
{
    Url = Resolve("Mqtt:Url", "MQTT_URL", "tcp://localhost:1883"),
    ClientId = Resolve("Mqtt:ClientId", "MQTT_CLIENT_ID", "smartgn-telemetry"),
    Username = ResolveOptional("Mqtt:Username", "MQTT_USERNAME"),
    Password = ResolveOptional("Mqtt:Password", "MQTT_PASSWORD"),
    Topic = Resolve("Mqtt:Topic", "MQTT_TOPIC", "smartgn/devices/+/telemetry"),
};

var influxSettings = new InfluxSettings
{
    Url = Resolve("Influx:Url", "INFLUX_URL", "http://localhost:8086"),
    Token = Resolve("Influx:Token", "INFLUX_TOKEN", string.Empty),
    Org = Resolve("Influx:Org", "INFLUX_ORG", "smartgn"),
    Bucket = Resolve("Influx:Bucket", "INFLUX_BUCKET", "telemetry"),
    Measurement = Resolve("Influx:Measurement", "INFLUX_MEASUREMENT", "telemetry"),
};

var integrationSettings = new IntegrationSettings
{
    InstallationBaseUrl = Resolve("Installation:BaseUrl", "INSTALLATION_BASE_URL", "http://localhost:8081"),
    AlertingEventsSinkUrl = Resolve("Alerting:EventsSinkUrl", "ALERTING_EVENTS_SINK_URL", "http://localhost:8085/internal/v1/events"),
    InternalServiceToken = ResolveOptional("Security:InternalServiceToken", "INTERNAL_SERVICE_TOKEN"),
};

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Health and readiness on the HTTP port; the private query contract on
    // its own HTTP/2 port (deployment target: HTTP/2 over TLS in-network).
    kestrel.ListenAnyIP(telemetrySettings.HttpPort);
    kestrel.ListenAnyIP(telemetrySettings.GrpcPort, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(telemetrySettings);
builder.Services.AddSingleton(mqttSettings);
builder.Services.AddSingleton(influxSettings);
builder.Services.AddSingleton(integrationSettings);

builder.Services.AddSingleton<ITelemetryStore>(_ =>
    new InfluxTelemetryStore(influxSettings, _.GetRequiredService<ILogger<InfluxTelemetryStore>>()));

builder.Services.AddSingleton<IDeviceAuthorizationClient>(_ =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(integrationSettings.InstallationBaseUrl),
        Timeout = TelemetryRules.RequestTimeout,
    };
    if (integrationSettings.InternalServiceToken is not null)
    {
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", integrationSettings.InternalServiceToken);
    }

    return new InstallationAuthorizationClient(httpClient);
});

builder.Services.AddSingleton(_ =>
    new CircuitBreaker(
        TelemetryRules.CircuitFailureThreshold,
        TelemetryRules.CircuitProbeInterval,
        TimeProvider.System));

builder.Services.AddSingleton<ITelemetryEventPublisher>(services =>
    new AlertingEventPublisher(
        new HttpClient { Timeout = TelemetryRules.RequestTimeout },
        integrationSettings.AlertingEventsSinkUrl,
        services.GetRequiredService<CircuitBreaker>(),
        services.GetRequiredService<ILogger<AlertingEventPublisher>>()));

builder.Services.AddSingleton(_ =>
    new TelemetryEventBuffer(TelemetryRules.EventBufferCapacity, TelemetryRules.EventBufferMaxAge));

builder.Services.AddSingleton<TelemetryIngestionService>();
builder.Services.AddSingleton<AppTelemetryQueryService>();

builder.Services.AddSingleton(services =>
    new MqttConnectionManager(mqttSettings, services.GetRequiredService<ILogger<MqttConnectionManager>>()));
builder.Services.AddHostedService<MqttTelemetryConsumer>();
builder.Services.AddHostedService<TelemetryEventFlushWorker>();

builder.Services.AddSingleton(_ => new InternalTokenInterceptor(integrationSettings));
builder.Services.AddGrpc(options => options.Interceptors.Add<InternalTokenInterceptor>());

var app = builder.Build();

// Actuator-style liveness and readiness used by Docker, compose and smoke.
app.MapGet("/health", () => Results.Text("UP", "text/plain"));

app.MapGet("/ready", async (ITelemetryStore store, MqttConnectionManager connection, CancellationToken cancellationToken) =>
{
    var influxUp = await store.IsHealthyAsync(cancellationToken);
    var payload = new
    {
        status = influxUp ? "UP" : "DOWN",
        influx = influxUp ? "UP" : "DOWN",
        mqtt = connection.IsConnected ? "UP" : "DOWN",
    };
    return influxUp
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGrpcService<TelemetryQueryGrpcService>();

app.Run();
