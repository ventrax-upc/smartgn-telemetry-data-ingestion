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

`GET /internal/v1/devices/{deviceId}/authorization?associationVersion={version}` with `Authorization: Bearer {INTERNAL_SERVICE_TOKEN}`, 2 s timeout. Telemetry requires the returned device, association version and `authorized` boolean to agree with the request before accepting the reading.

| Status | Meaning |
| --- | --- |
| 200 | Device is provisioned and may publish |
| 401 | Our service token was rejected |
| 403 / 404 | Device forbidden / unknown |
| other / timeout / network error | Reading rejected (`installation_unavailable`, `installation_timeout`) |

## Outbound: Alerting event sink

`POST {ALERTING_EVENTS_SINK_URL}` with an Alerting `TelemetryEventResource` JSON body (camelCase): `deviceId`, `eventId`, `propertyId`, `supplyPointId`, `accountId`, `measuredAt`, `receivedAt`, and available `consumptionM3`, `pressureKpa`, `gasPresence` and `valveOpen` metrics. The account and property identifiers come from the confirmed versioned association ledger. The shared `INTERNAL_SERVICE_TOKEN` is sent as a bearer credential. The default path is `/internal/v1/telemetry-events`. Delivery retries are bounded to 360 pending events in memory with 60 minutes of retention; a process restart clears pending events.

## Inbound private HTTP contracts

These operations are documented in Swagger for service-to-service consumers. When INTERNAL_SERVICE_TOKEN is configured, each requires Authorization: Bearer {INTERNAL_SERVICE_TOKEN}; local runs with an empty token leave them open.

| Method and route | Purpose |
| --- | --- |
| POST /internal/v1/device-associations | Confirm a versioned device association sent by Installation. |
| POST /internal/v1/devices/status-query | Query last-contact status for scoped devices. |
| POST /internal/v1/devices/{deviceId}/buffer-drain | Confirm whether a specific association has pending readings. |

## Private gRPC: TelemetryQueryService (port 9084)

The gRPC contract is guarded by `authorization: Bearer {INTERNAL_SERVICE_TOKEN}` when a token is configured.

| RPC | Request | Behavior |
| --- | --- | --- |
| `GetReadingsBatch` | `device_ids` or `supply_point_ids` plus `account_id`, `from_unix_ms`, `to_unix_ms`, `limit` | Scoped readings in range, newest first; invalid range or scoped request without an account UUID → `InvalidArgument` |
| `GetLatestReadings` | `device_ids` or `supply_point_ids` plus `account_id`, `limit` | Latest reading per device or account-owned supply point |

`limit` defaults to 100 and is capped at 1000; identifiers not matching the identifier pattern are dropped before the query. `Reading` carries `metrics` (map), `freshness` (`current` / `delayed`, threshold 30 s), timestamps in Unix milliseconds and `association_version`.

Monitoring sends the authenticated account UUID with meter UUIDs as `supply_point_ids`. Telemetry resolves matching device and association versions from its versioned association ledger, then filters historical readings by account, `measuredAt` and the association validity interval. Ownership transfers therefore do not expose a prior account's readings to the new owner. Replies include `supply_point_id` so Monitoring keeps the public meter identifier when device IDs change.


## HTTP health (port 8085)

| Endpoint | Response |
| --- | --- |
| `GET /health` | `200 UP` (plain text) |
| `GET /ready` | `200 {status, influx, mqtt}` when InfluxDB answers; `503` with the same body otherwise |
