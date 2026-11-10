# Contracts

Three surfaces: the inbound MQTT payload, the private gRPC read contract and the HTTP health endpoints, plus the two outbound integrations.

## Inbound: MQTT telemetry payload

Topic `smartgn/devices/{deviceId}/telemetry`, MQTT 5, QoS 1. The topic device must equal the payload device; mismatches are rejected.

```json
{
  "schemaVersion": 1,
  "deviceId": "meter-001",
  "eventId": "8f14e45f-ceea-467f-a0b1-1a2b3c4d5e6f",
  "sequenceNumber": 1042,
  "measuredAt": "2026-05-10T12:00:00Z",
  "associationVersion": 3,
  "metrics": { "voltage": 220.4, "activeEnergy": 12.5 }
}
```

| Field | Rule |
| --- | --- |
| `schemaVersion` | Required, must be `1` |
| `deviceId`, `eventId` | Required, `^[A-Za-z0-9._-]{1,64}$` (eventId up to 128) |
| `sequenceNumber` | Required integer ≥ 0 |
| `measuredAt` | Required RFC 3339 timestamp; more than 5 s in the future is rejected |
| `associationVersion` | Optional integer ≥ 0, defaults to 0 |
| `metrics` | Required non-empty object; names `^[a-zA-Z][a-zA-Z0-9_]{0,31}$`, finite numbers only |

Stable rejection codes (logs and tests): `payload_required`, `invalid_json`, `schema_version_required`, `unsupported_schema_version`, `deviceId_required`/`deviceId_invalid`, `eventId_required`/`eventId_invalid`, `sequence_number_invalid`, `measured_at_invalid`, `association_version_invalid`, `metrics_required`, `metric_name_invalid`, `metric_value_invalid`, plus pipeline codes `measured_at_in_future`, `identifier_invalid`, `device_unknown`, `device_forbidden`, `service_token_rejected`, `installation_unavailable`, `installation_timeout`, `duplicate_event_id`, `store_unavailable`.

## Outbound: Installation authorization

`GET /internal/v1/devices/{deviceId}/authorization` with `Authorization: Bearer {INTERNAL_SERVICE_TOKEN}`, 2 s timeout.

| Status | Meaning |
| --- | --- |
| 200 | Device is provisioned and may publish |
| 401 | Our service token was rejected |
| 403 / 404 | Device forbidden / unknown |
| other / timeout / network error | Reading rejected (`installation_unavailable`, `installation_timeout`) |

## Outbound: Alerting event sink

`POST {ALERTING_EVENTS_SINK_URL}` with a `TelemetryProcessed` JSON body (camelCase): `deviceId`, `eventId`, `sequenceNumber`, `measuredAt`, `processedAt`, `associationVersion`, `metrics`. Delivery is at-least-once: failures are requeued in the local outbox (batch of 50, backoff 1-2-4-8-16-30 s).

## Private gRPC: TelemetryQueryService (port 9084)

`Protos/telemetry_query.proto`, guarded by `authorization: Bearer {INTERNAL_SERVICE_TOKEN}` when a token is configured.

| RPC | Request | Behavior |
| --- | --- | --- |
| `GetReadingsBatch` | `device_ids`, `from_unix_ms`, `to_unix_ms`, `limit` | Readings in range, newest first; invalid range → `InvalidArgument`; empty device list → empty response |
| `GetLatestReadings` | `device_ids`, `limit` | Latest reading per device |

`limit` defaults to 100 and is capped at 1000; identifiers not matching the identifier pattern are dropped before the query. `Reading` carries `metrics` (map), `freshness` (`current` / `delayed`, threshold 30 s), timestamps in Unix milliseconds and `association_version`.

## HTTP health (port 8084)

| Endpoint | Response |
| --- | --- |
| `GET /health` | `200 UP` (plain text) |
| `GET /ready` | `200 {status, influx, mqtt}` when InfluxDB answers; `503` with the same body otherwise |
