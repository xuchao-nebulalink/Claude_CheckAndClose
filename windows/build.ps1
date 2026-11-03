param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$repository = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repository "dist/ClaudeControl-windows-$Runtime"
New-Item -ItemType Directory -Force $output | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'ClaudeControl.Windows/ClaudeControl.Windows.csproj') `
    -c Release -r $Runtime --self-contained true -o $output `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item (Join-Path $PSScriptRoot 'README.md') $output -Force
$licenseOutput = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force $licenseOutput | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'licenses/*') $licenseOutput -Recurse -Force
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$isNativeWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -and $Runtime -eq "win-$architecture"
if ($isNativeWindows) {
    $json = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-control-smoke-" + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $process = Start-Process (Join-Path $output 'ClaudeControl.exe') -ArgumentList @('--status-json', ('"' + $json + '"')) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Read-only published-app smoke failed (exit $($process.ExitCode))." }
        if (-not (Test-Path $json)) { throw 'Published app did not create its status JSON.' }
        $status = Get-Content $json -Raw | ConvertFrom-Json
        if ($status.scan_complete -ne $true -or $null -eq $status.remaining_count -or $null -eq $status.processes -or
            $null -eq $status.cowork_service -or $null -eq $status.cowork_service.installed -or
            $null -eq $status.cowork_service.confirmed_stopped -or $null -eq $status.confirmed_closed -or
            $status.network_isolation -ne $false) {
            throw 'Published-app status JSON is incomplete.'
        }
        Write-Host "Published-app read-only smoke passed ($($status.remaining_count) detected processes, Cowork service installed: $($status.cowork_service.installed))."
    } finally {
        if (Test-Path $json) { Remove-Item $json }
    }
} else {
    Write-Host 'Published-app execution skipped: this build host does not match the Windows runtime. CI must smoke-test on the matching native Windows runner.'
}
$zip = "$output.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip
Write-Host "Created $zip"
