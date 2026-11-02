$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
dotnet run --project (Join-Path $PSScriptRoot 'ClaudeControl.Tests/ClaudeControl.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Windows/core tests failed.' }
