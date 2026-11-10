param([switch]$SkipRestore)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
if(-not $SkipRestore){dotnet restore Smartgn.Telemetry.slnx}
if($LASTEXITCODE -ne 0){throw 'dotnet restore failed'}
dotnet build Smartgn.Telemetry.slnx -c Release --no-restore
if($LASTEXITCODE -ne 0){throw 'dotnet build failed'}
dotnet test Smartgn.Telemetry.slnx -c Release --no-build --logger "console;verbosity=normal"
if($LASTEXITCODE -ne 0){throw 'dotnet test failed'}
