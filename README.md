# SmartGN Telemetry & Data Ingestion

MQTT ingestion, validation, deduplication and normalization of device telemetry, durable storage in a time-series repository, and the private gRPC read contract consumed by Monitoring & Analytics.

.NET 10 (C#), MQTTnet 5/QoS 1 consumer, InfluxDB retention 30 days, local outbox for `TelemetryProcessed` events and gRPC `TelemetryQueryService`. Source projects use `Smartgn.Telemetry` namespaces. This bounded context owns its data; integration uses HTTP and durable events.

## Run locally

Requirements: Docker Desktop Linux engine and PowerShell 7.

```powershell
./scripts/start-local.ps1
./scripts/test.ps1
./scripts/smoke-local.ps1
```

`start-local.ps1` creates `.env` from `.env.example` on the first run, starts InfluxDB and Mosquitto, builds the service and waits for `/ready`. Secrets in a generated `.env` are for local use only.

| Endpoint | Address |
| --- | --- |
| Health / readiness | http://127.0.0.1:8084 (`/health`, `/ready`) |
| gRPC TelemetryQueryService | 127.0.0.1:9084 |
| InfluxDB | http://127.0.0.1:18086 |
| MQTT broker | tcp://127.0.0.1:1885 |

Ports 8084 (HTTP) and 9084 (gRPC) are the service ports of the shared composition; InfluxDB is published on 18086 and Mosquitto on 1885 to avoid collisions with sibling services.

## Processing pipeline

1. **MQTT consumer** subscribes to `smartgn/devices/+/telemetry` (MQTT 5, QoS 1, ack only after handling) with reconnect backoff 1-2-4-8-16-30 s.
2. **Validation** parses the payload contract and rejects unknown devices through `GET /internal/v1/devices/{deviceId}/authorization` on Installation.
3. **Deduplication** keys on `deviceId` + `eventId`, so QoS 1 redelivery never stores a reading twice.
4. **Normalization** derives freshness (`current` / `delayed` beyond 30 s) and a stable reading model.
5. **Storage** writes points to InfluxDB with 30 days retention (QAS16).
6. **Outbox** announces `TelemetryProcessed` to Alerting & Notifications with a bounded buffer (360 events / 60 minutes, resend ≤ 30 s) behind a circuit breaker (2 s timeout, open after 5 consecutive failures, probe after 30 s).

## Layout

```
src/Smartgn.Telemetry      Domain, Application, Infrastructure, Grpc, Program.cs
tests/Smartgn.Telemetry.Tests   xUnit tests with in-memory fakes (no external dependencies)
protos/telemetry_query.proto    private read contract (also under src/.../Protos)
docs/     architecture, contracts, local development
scripts/  start-local, test, smoke-local
tools/    health.sh, mosquitto.conf
```

## Configuration

Environment variables override `src/Smartgn.Telemetry/appsettings.json`:

| Variable | Purpose | Default |
| --- | --- | --- |
| `PORT` / `GRPC_PORT` | HTTP and gRPC listeners | 8084 / 9084 |
| `MQTT_URL`, `MQTT_TOPIC` | Broker URL and topic filter | `tcp://localhost:1883`, `smartgn/devices/+/telemetry` |
| `INFLUX_URL`, `INFLUX_TOKEN`, `INFLUX_ORG`, `INFLUX_BUCKET` | Time-series repository | `http://localhost:8086`, `smartgn`, `telemetry` |
| `INSTALLATION_BASE_URL` | Device authorization contract | `http://localhost:8081` |
| `ALERTING_EVENTS_SINK_URL` | `TelemetryProcessed` sink | `http://localhost:8085/internal/v1/events` |
| `INTERNAL_SERVICE_TOKEN` | Service-to-service bearer token (also guards the gRPC contract) | empty (open in local development) |

## Tests

`./scripts/test.ps1` builds the solution and runs the xUnit suite: payload parser, domain rules, event buffer, circuit breaker, ingestion pipeline, query read model and protobuf mapping. Fakes replace InfluxDB, Installation and Alerting, so the suite runs without Docker.
