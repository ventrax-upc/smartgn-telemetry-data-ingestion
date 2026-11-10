var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Placeholder host: the real MQTT / InfluxDB / gRPC wiring lands with the
// "service host" feature branch.
app.MapGet("/health", () => Results.Text("UP", "text/plain"));

app.Run();
