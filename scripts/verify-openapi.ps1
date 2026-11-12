param([string]$OpenApiUrl = $env:OPENAPI_URL)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
if($OpenApiUrl){$env:OPENAPI_URL=$OpenApiUrl}
python tools/verify-openapi.py
if($LASTEXITCODE -ne 0){throw 'OpenAPI verification failed.'}
