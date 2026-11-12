$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$base='http://127.0.0.1:8085'

Write-Host '1/5 Health endpoint'
$health=(Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "$base/health").Content
if($health -notmatch 'UP'){throw "/health did not answer UP: $health"}

Write-Host '2/5 Readiness endpoint'
$ready=Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "$base/ready"
$state=$ready.Content | ConvertFrom-Json
if($state.status -ne 'UP' -or $state.influx -ne 'UP' -or $state.mqtt -ne 'UP'){
  throw "/ready reported $($ready.Content)"
}

Write-Host '3/5 Spring-compatible Swagger and OpenAPI'
$ui=Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "$base/swagger-ui.html"
if($ui.StatusCode -ne 200){throw "Swagger UI returned HTTP $($ui.StatusCode)"}
$api=Invoke-RestMethod -TimeoutSec 10 "$base/v3/api-docs"
if($api.info.title -ne 'SmartGN - Telemetry & Data Ingestion' -or $api.info.version -ne 'v1'){
  throw 'OpenAPI metadata does not match Telemetry v1.'
}
if(-not $api.paths.PSObject.Properties['/internal/v1/device-associations']){
  throw 'OpenAPI is missing the device association contract.'
}
if(-not $api.components.securitySchemes.serviceAuth -or -not $api.components.securitySchemes.bearerAuth){
  throw 'OpenAPI is missing the shared bearerAuth/serviceAuth schemes.'
}

Write-Host '4/5 gRPC query port'
$tcp=Test-NetConnection -ComputerName 127.0.0.1 -Port 9084 -WarningAction SilentlyContinue
if(-not $tcp.TcpTestSucceeded){throw 'gRPC port 9084 is not accepting connections'}

Write-Host '5/5 MQTT ingestion pipeline'
$payload=@{
  schemaVersion=1
  deviceId='smoke-device'
  eventId=[Guid]::NewGuid().ToString('N')
  sequenceNumber=1
  measuredAt=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
  associationVersion=1
  metrics=@{voltage=220.4;activeEnergy=1.25}
}|ConvertTo-Json -Compress
$network=docker network ls --format '{{.Name}}' | Where-Object {$_ -like 'smartgn-telemetry*'} | Select-Object -First 1
if(-not $network){throw 'compose network not found — start the stack with scripts/start-local.ps1'}
docker run --rm --network $network eclipse-mosquitto:2 mosquitto_pub -h mqtt -t 'smartgn/devices/smoke-device/telemetry' -m $payload
if($LASTEXITCODE -ne 0){throw 'mosquitto_pub failed'}
Start-Sleep -Seconds 3
# The service must survive the message: an unauthorized device is rejected by
# the ingestion pipeline without taking the consumer down.
$after=Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "$base/ready"
$state=$after.Content | ConvertFrom-Json
if($state.status -ne 'UP'){throw "service degraded after publishing: $($after.Content)"}

Write-Host ''
Write-Host 'Smoke OK: health, readiness, Swagger/OpenAPI, gRPC port and MQTT ingestion are alive.'
Write-Host 'Note: without Installation running the reading is rejected as unauthorized (expected).'
