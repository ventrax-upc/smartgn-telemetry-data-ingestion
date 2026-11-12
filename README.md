# SmartGN Telemetry & Data Ingestion

## Overview

Authorizes, validates and deduplicates device readings received over MQTT. Stores readings in InfluxDB, forwards normalized events to Alerting and serves private gRPC queries on port 9084.

**Stack**  .NET 10, InfluxDB, MQTT, gRPC.

## Run locally

Requirements are Docker Desktop with the Linux engine and PowerShell 7. Keep the SmartGN repositories as siblings for the integrated stack.

```powershell
./scripts/start-local.ps1
```

Run `./scripts/start-integrated.ps1` from the sibling `smartgn-api-gateway` repository.

| Endpoint | Local URL |
| --- | --- |
| Swagger UI | [Open](http://localhost:8085/swagger/index.html) |
| OpenAPI | [JSON](http://localhost:8085/v3/api-docs) |
| Health | [Open](http://localhost:8085/ready) |

## Configuration

Standalone settings use the variables in `.env.example`. Copy it to an ignored `.env` when the local startup script requires it. Integrated credentials and generated files are stored under `.local` in the Gateway repository.

## Verification

```powershell
./scripts/test.ps1
./scripts/smoke-local.ps1
```

Run the integrated smoke from the Gateway repository to verify cross-service workflows.

## Repository layout

- `src/Smartgn.Telemetry` contains the service implementation.
- `tests/Smartgn.Telemetry.Tests` contains service tests.
- `docs` contains service documentation.
- `scripts` contains local startup and verification commands.
- `tools` contains verification helpers and supporting assets.
- `fixtures/db.json` defines demo fixtures for local verification.

## Documentation

- [Local development](docs/local-development.md)
- [Architecture](docs/architecture.md)
- [Contracts](docs/contracts.md)
- [OpenAPI export](docs/openapi.json)

## License

Distributed under the [MIT License](LICENSE).
