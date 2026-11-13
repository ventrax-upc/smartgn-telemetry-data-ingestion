param([switch]$Rebuild)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
if(-not (Test-Path .env)){
  Copy-Item .env.example .env
  Write-Host 'Created .env from .env.example — change the secrets before any shared use.'
}
$arguments=@('compose','up','-d')
if($Rebuild){$arguments+='--build'}
& docker @arguments
if($LASTEXITCODE -ne 0){throw 'docker compose up failed'}
Write-Host 'Waiting for the telemetry service to become ready...'
for($i=0;$i -lt 60;$i++){
  try{
    $response=Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://127.0.0.1:8085/ready
    if($response.StatusCode -eq 200){Write-Host 'Telemetry service is UP (InfluxDB reachable).';break}
  }catch{}
  Start-Sleep -Seconds 2
}
if($i -ge 60){
  docker compose logs --tail 60 telemetry
  throw 'Telemetry service did not become ready'
}
Write-Host ''
Write-Host 'Service      http://127.0.0.1:8085  (/health, /ready)'
Write-Host 'gRPC         127.0.0.1:9084  (TelemetryQueryService)'
Write-Host 'InfluxDB     http://127.0.0.1:18086'
Write-Host 'MQTT broker  tcp://127.0.0.1:1885  (topic smartgn/devices/{deviceId}/telemetry)'
