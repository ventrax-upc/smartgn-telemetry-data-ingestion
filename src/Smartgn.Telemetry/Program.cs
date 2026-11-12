using Grpc.AspNetCore;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Configuration;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Grpc;
using Smartgn.Telemetry.Infrastructure;
using Smartgn.Telemetry.Infrastructure.OpenApi;
using Smartgn.Telemetry.Interfaces.Rest;
using AppTelemetryQueryService = Smartgn.Telemetry.Application.TelemetryQueryService;

var builder = WebApplication.CreateBuilder(args);
await RemoteServiceBootstrap.LoadConfigServerAsync(builder.Configuration);
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

    return (environmentVariable is null ? null : configuration[environmentVariable])
        ?? configuration[configKey]
        ?? fallback;
}

string? ResolveOptional(string configKey, string? environmentVariable)
{
    var value = Resolve(configKey, environmentVariable, string.Empty);
    return value.Length > 0 ? value : null;
}

var telemetrySettings = new TelemetrySettings
{
    HttpPort = int.TryParse(Resolve("Telemetry:HttpPort", "PORT", "8085"), out var httpPort) ? httpPort : 8085,
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
    Url = Resolve("Influx:Url", "INFLUX_URL", "http://localhost:18086"),
    Token = Resolve("Influx:Token", "INFLUX_TOKEN", string.Empty),
    Org = Resolve("Influx:Org", "INFLUX_ORG", "smartgn"),
    Bucket = Resolve("Influx:Bucket", "INFLUX_BUCKET", "telemetry"),
    Measurement = Resolve("Influx:Measurement", "INFLUX_MEASUREMENT", "telemetry"),
    AssociationMeasurement = Resolve("Influx:AssociationMeasurement", "INFLUX_ASSOCIATION_MEASUREMENT", "device_associations"),
};

var integrationSettings = new IntegrationSettings
{
    InstallationBaseUrl = Resolve("Installation:BaseUrl", "INSTALLATION_BASE_URL", "http://localhost:8081"),
    AlertingEventsSinkUrl = Resolve("Alerting:EventsSinkUrl", "ALERTING_EVENTS_SINK_URL", "http://localhost:8086/internal/v1/telemetry-events"),
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
builder.Services.AddSingleton<IDeviceAssociationStore>(_ =>
    new InfluxDeviceAssociationStore(influxSettings, _.GetRequiredService<ILogger<InfluxDeviceAssociationStore>>()));

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
        CreateAlertingHttpClient(integrationSettings),
        integrationSettings.AlertingEventsSinkUrl,
        services.GetRequiredService<IDeviceAssociationStore>(),
        services.GetRequiredService<CircuitBreaker>(),
        services.GetRequiredService<ILogger<AlertingEventPublisher>>()));

builder.Services.AddSingleton(_ =>
    new TelemetryEventBuffer(TelemetryRules.EventBufferCapacity, TelemetryRules.EventBufferMaxAge));

builder.Services.AddSingleton<TelemetryIngestionService>();
builder.Services.AddSingleton<AppTelemetryQueryService>();
builder.Services.AddSingleton<DeviceOperations>();

builder.Services.AddSingleton(services =>
    new MqttConnectionManager(mqttSettings, services.GetRequiredService<ILogger<MqttConnectionManager>>()));
builder.Services.AddHostedService<MqttTelemetryConsumer>();
builder.Services.AddHostedService<TelemetryEventFlushWorker>();
builder.Services.AddHttpClient(nameof(AdminRegistrationWorker), client =>
    client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHostedService<AdminRegistrationWorker>();

builder.Services.AddSingleton(_ => new InternalTokenInterceptor(integrationSettings));
builder.Services.AddGrpc(options => options.Interceptors.Add<InternalTokenInterceptor>());
builder.Services.AddScoped<InternalServiceTokenFilter>();
builder.Services
    .AddControllers(options => options.Filters.AddService<InternalServiceTokenFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SmartGN - Telemetry & Data Ingestion",
        Version = "v1",
        Description = "MQTT ingestion, validation, deduplication and normalization of device telemetry. Exposes private device operations over authenticated HTTP and a private gRPC query contract.",
        License = new OpenApiLicense { Name = "MIT" },
    });
    options.AddSecurityDefinition("bearerAuth", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Current IAM JWT for public user APIs.",
    });
    options.AddSecurityDefinition("serviceAuth", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Private service credential, independent of user JWTs. Internal routes must remain outside public gateway routing.",
    });
    options.TagActionsBy(description => new[] { description.GroupName ?? "Internal telemetry" });
    options.DocumentFilter<TelemetryTagsDocumentFilter>();
    options.OperationFilter<ServiceAuthOperationFilter>();
    options.EnableAnnotations();

    var xmlName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlName);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: false);
    }
});

var app = builder.Build();

// Keep the same Swagger URLs as the Spring Boot services.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/v3/api-docs")
    {
        context.Request.Path = "/swagger/v1/swagger.json";
    }

    await next();
});
app.UseStaticFiles();
app.UseSwagger(options => options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1);
app.UseSwaggerUI(options =>
{
    options.InjectStylesheet("./smartgn.css");
    options.DocumentTitle = "SmartGN | Telemetry & Data Ingestion";
    options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
    options.DefaultModelsExpandDepth(0);
    options.DisplayRequestDuration();
    options.SwaggerEndpoint("/v3/api-docs", "SmartGN - Telemetry & Data Ingestion v1");
});

// Actuator-style liveness and readiness used by Docker, compose and smoke.
app.MapGet("/health", () => Results.Text("UP", "text/plain"))
    .ExcludeFromDescription();

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
})
    .ExcludeFromDescription();

app.MapGrpcService<TelemetryQueryGrpcService>();
app.MapControllers();
app.MapGet("/swagger-ui.html", () => Results.Redirect("/swagger/index.html"))
    .ExcludeFromDescription();

app.Run();

static HttpClient CreateAlertingHttpClient(IntegrationSettings settings)
{
    var client = new HttpClient { Timeout = TelemetryRules.RequestTimeout };
    if (!string.IsNullOrWhiteSpace(settings.InternalServiceToken))
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.InternalServiceToken);
    }

    return client;
}
