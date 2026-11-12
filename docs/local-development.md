# Local development

## Requirements

- Docker Desktop with the Linux engine
- PowerShell 7
- .NET SDK 10 (optional — the compose build uses the SDK image; a host SDK is only needed to run `scripts/test.ps1` without Docker)

## Commands

```powershell
./scripts/start-local.ps1          # .env from .env.example, compose up, wait for /ready
./scripts/start-local.ps1 -Rebuild # rebuild the service image
./scripts/test.ps1                 # restore, build (Release), xUnit suite
./scripts/smoke-local.ps1          # health, readiness, Swagger, gRPC and MQTT publish
./scripts/export-openapi.ps1       # refresh docs/openapi.json from /v3/api-docs
./scripts/verify-openapi.ps1       # verify against tools/service.json
docker compose logs -f telemetry
docker compose down                 # keeps the InfluxDB volume
```

Never delete the `influx-data` volume to bypass a problem; the bucket is initialized by `DOCKER_INFLUXDB_INIT_*` only on the first start.

## Ports

| Service | Host port | Container port |
| --- | --- | --- |
| Telemetry HTTP | 8085 | 8085 |
| Swagger UI | 8085 | /swagger-ui.html |
| OpenAPI document | 8085 | /v3/api-docs |
| Telemetry gRPC | 9084 | 9084 |
| InfluxDB | 18086 | 8086 |
| Mosquitto | 1885 | 1883 |

The table shows the standalone Compose ports. The integrated Compose publishes Telemetry HTTP on host port 8085 and keeps gRPC, MQTT and database ports private. Containers connect using service DNS. Standalone Alerting publishes host port 8086.

## Running against sibling services

`INSTALLATION_BASE_URL` and `ALERTING_EVENTS_SINK_URL` point to sibling services. In the integrated Compose they resolve to Installation and Alerting service DNS names. Standalone defaults use host ports `8081` and `8086`. When Installation or its required `associationVersion` authorization contract is unavailable, ingestion rejects the reading rather than recording an unauthorized device.

In the integrated Compose, `SMARTGN_CONFIG_URL` loads `smartgn-telemetry/local` from Config Server during startup, and `SMARTGN_ADMIN_URL` enables authenticated registration of `/ready` in Admin. `SMARTGN_CONFIG_PROFILE` selects another profile. Local environment variables override remotely loaded values. These settings are omitted in standalone runs.

## Publish a reading by hand

Use a device and association version that Installation authorizes. The identifiers below illustrate the payload shape.

```powershell
$payload = @{
  schemaVersion = 1; deviceId = 'meter-001'; eventId = [Guid]::NewGuid().ToString('N')
  sequenceNumber = 1; measuredAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
  associationVersion = 1; metrics = @{ voltage = 220.4 }
} | ConvertTo-Json -Compress
docker run --rm --network smartgn-telemetry_default eclipse-mosquitto:2 `
  mosquitto_pub -h mqtt -t 'smartgn/devices/meter-001/telemetry' -m $payload
```

Verify with the Influx UI on `http://127.0.0.1:18086` (admin / password from `.env`) or by querying the bucket `telemetry`.

## Project layout

`src/Smartgn.Telemetry` follows the ports-and-adapters shape used by the sibling services: `Domain` (entities and rules), `Application` (use cases and outbound ports), `Infrastructure` (InfluxDB, MQTT, HTTP adapters), `Grpc` (private contract). Tests live in `tests/Smartgn.Telemetry.Tests` and use in-memory fakes, so `dotnet test` needs no Docker.
