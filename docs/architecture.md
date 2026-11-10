# Architecture

Telemetry & Data Ingestion is one of the seven bounded contexts of SmartGN (report chapter IV). It receives device readings from the MQTT broker, validates, deduplicates and normalizes them, stores them in a time-series repository and announces `TelemetryProcessed` to Alerting & Notifications, exposing a private gRPC read contract to Monitoring & Analytics.

## Components

| Component | Report name | Implementation |
| --- | --- | --- |
| MQTT connection | `MqttConnectionManager` | `Infrastructure/MqttConnectionManager.cs`, one client per process, QoS 1, backoff 1-2-4-8-16-30 s |
| MQTT consumer | Telemetry Consumer | `Infrastructure/MqttTelemetryConsumer.cs`, topic `smartgn/devices/+/telemetry` |
| Validation / dedup / normalization | Ingestion pipeline | `Domain/TelemetryPayloadParser.cs`, `Application/TelemetryIngestionService.cs` |
| Time-series repository | InfluxDB store | `Infrastructure/InfluxTelemetryStore.cs`, 30-day retention (QAS16) |
| Event outbox | TelemetryProcessed publisher | `Application/TelemetryEventBuffer.cs` + `Infrastructure/TelemetryEventFlushWorker.cs` |
| Circuit breaker | Alerting guard | `Application/CircuitBreaker.cs` (QAS18: 2 s, 5 failures, 30 s probe) |
| Read contract | `TelemetryQueryService` | `Grpc/TelemetryQueryGrpcService.cs` on port 9084 |

## Dependencies

- **MQTT broker** (EMQX in the deployment target, Mosquitto locally): inbound readings.
- **Installation & Device Management**: `GET /internal/v1/devices/{deviceId}/authorization` with the shared `INTERNAL_SERVICE_TOKEN`. Any unavailable dependency rejects the reading — unknown connectivity is never treated as a successful business operation.
- **InfluxDB**: durable storage of normalized readings.
- **Alerting & Notifications**: `TelemetryProcessed` events over the internal HTTP sink, buffered locally.

## Data flow

```
device --MQTT QoS1--> consumer --> parser --> authorization (Installation)
                                        |               |
                                        v               v
                                  dedup (eventId) --> InfluxDB (30d)
                                                          |
                                                          v
                                                  outbox (360 / 60 min)
                                                          |
                                          circuit breaker v (QAS18)
                                                          v
                                              Alerting sink (TelemetryProcessed)

Monitoring & Analytics --gRPC--> TelemetryQueryService (GetReadingsBatch, GetLatestReadings)
```

## Decisions

- **At-least-once without duplicates**: QoS 1 acknowledges only after the pipeline handled the message; redelivery is absorbed by `deviceId` + `eventId` deduplication.
- **Conservative authorization**: a device that Installation cannot confirm is rejected before reaching the repository.
- **Bounded outbox**: the buffer evicts oldest-first at 360 events or 60 minutes, so an unreachable Alerting sink cannot grow the process without bound; retries never wait more than 30 s (QAS13).
- **Separate gRPC listener**: health on 8084, private contract on 9084 with HTTP/2; the deployment target terminates TLS inside the service network.
- **Flux safety**: identifiers interpolated into Flux queries are validated against `^[A-Za-z0-9._-]{1,64}$`, so no raw input reaches a query fragment.

## Quality attribute scenarios (report)

| QAS | Behavior here |
| --- | --- |
| QAS6 | Ingestion decouples the broker from the repository through the outbox |
| QAS13 | Local buffer 360 events / 60 min, resend ≤ 30 s |
| QAS15 | `measuredAt` older than 30 s is stored as `delayed`; future skew > 5 s is rejected |
| QAS16 | Raw readings retained 30 days in InfluxDB |
| QAS18 | Outbound HTTP timeout 2 s, circuit opens after 5 consecutive failures, probe after 30 s |
