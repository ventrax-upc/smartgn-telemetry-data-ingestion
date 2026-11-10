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
./scripts/smoke-local.ps1          # /health, /ready, gRPC port, MQTT publish
docker compose logs -f telemetry
docker compose down                 # keeps the InfluxDB volume
```

Never delete the `influx-data` volume to bypass a problem; the bucket is initialized by `DOCKER_INFLUXDB_INIT_*` only on the first start.

## Ports

| Service | Host port | Container port |
| --- | --- | --- |
| Telemetry HTTP | 8084 | 8084 |
| Telemetry gRPC | 9084 | 9084 |
| InfluxDB | 18086 | 8086 |
| Mosquitto | 1885 | 1883 |

Host ports are offset from the shared composition (8081 Installation, 8083 events sink, 8086 InfluxDB default) so this stack can run alone or beside the sibling services.

## Running against sibling services

`INSTALLATION_BASE_URL` and `ALERTING_EVENTS_SINK_URL` default to `http://host.docker.internal:8081` and `http://host.docker.internal:8085/internal/v1/events`. When Installation is not running, every reading is rejected as `installation_unavailable` — this is intended: the pipeline stays alive and the smoke test still passes.

## Publish a reading by hand

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
