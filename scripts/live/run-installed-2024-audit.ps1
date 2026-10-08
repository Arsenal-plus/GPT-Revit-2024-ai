[CmdletBinding()]
param(
    [ValidateSet('RUS','ENU')][string]$Language = 'RUS',
    [Parameter(Mandatory)][string]$RunRoot,
    [int]$TimeoutSeconds = 1200,
    [string]$ProbeServer,
    [switch]$UseInstalled,
    [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$revit = 'C:\Program Files\Autodesk\Revit 2024\Revit.exe'
if ((Get-Item -LiteralPath $revit).VersionInfo.FileVersion -ne '24.0.4.427') { throw 'This audit is pinned to installed build 24.0.4.427. It never updates Revit.' }
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit normally before starting an isolated audit session.' }
$run = [IO.Path]::GetFullPath($RunRoot)
if (Test-Path -LiteralPath $run) { throw 'RunRoot must be a new directory.' }
$addinDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
$installed = Join-Path $addinDir 'Horizun.addin'
$aside = Join-Path $addinDir 'Horizun.addin.audit-aside'
$manifest = Join-Path $addinDir 'Horizun-audit-2024.addin'
if ((Test-Path -LiteralPath $aside) -or (Test-Path -LiteralPath $manifest)) { throw 'An earlier audit manifest is present. Inspect and restore it before another run.' }
$source = Join-Path $repo 'src\Horizun.Revit\bin\Release'
if ($UseInstalled) { $source = Join-Path $addinDir 'Horizun' }
$harness = Join-Path $repo 'tests\Horizun.Revit2024.Live\bin\Release\net48\Horizun.Revit2024.Live.dll'
if (!(Test-Path -LiteralPath $harness)) { throw 'Build the Revit 2024 add-in and live test project first.' }
. (Join-Path $repo 'scripts\horizun-deploy.lib.ps1')
Assert-HorizunTfm -DllPath (Join-Path $source 'Horizun.Revit.dll') -Year 2024
$payload = Join-Path $run 'addin'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
if (!$UseInstalled) { Copy-Item -Path (Join-Path $source '*') -Destination $payload -Recurse }
Copy-Item -LiteralPath $harness -Destination $payload
$bridgeDll = Join-Path $(if ($UseInstalled) { $source } else { $payload }) 'Horizun.Revit.dll'
$snapshot = if (Test-Path -LiteralPath $installed) { (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash } else { $null }
$moved = $false
$process = $null
try {
    if ($snapshot) { Move-Item -LiteralPath $installed -Destination $aside; $moved = $true }
    $xmlPath = [Security.SecurityElement]::Escape($payload)
    $xmlBridge = [Security.SecurityElement]::Escape($bridgeDll)
    @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application"><Name>Horizun MCP audit build</Name><Assembly>$xmlBridge</Assembly><AddInId>b8e5a2f0-3c1d-4e6a-9f2b-7a4c8d1e5f30</AddInId><FullClassName>Horizun.Revit.App</FullClassName><VendorId>HRZN</VendorId><VendorDescription>Horizun local audit</VendorDescription></AddIn>
  <AddIn Type="Application"><Name>Horizun 2024 regression tests</Name><Assembly>$xmlPath\Horizun.Revit2024.Live.dll</Assembly><AddInId>737e6b29-865a-43b9-9b50-f2f8e32f5ab8</AddInId><FullClassName>Horizun.Revit2024.Live.AuditApp</FullClassName><VendorId>HRZN</VendorId><VendorDescription>Isolated local regression tests</VendorDescription></AddIn>
</RevitAddIns>
"@ | Set-Content -LiteralPath $manifest -Encoding UTF8
    $env:HORIZUN_AUDIT_RUN_ROOT = $run
    $env:HORIZUN_AUDIT_KEEP_OPEN = if ($KeepOpen -or $ProbeServer) { '1' } else { '0' }
    $process = Start-Process -FilePath $revit -ArgumentList '/language', $Language -PassThru -WindowStyle Hidden
    [ordered]@{ pid=$process.Id; started=$process.StartTime.ToUniversalTime().ToString('o'); revit=$revit; language=$Language; installed_manifest_sha256=$snapshot; payload=$payload; bridge_dll=$bridgeDll; bridge_sha256=(Get-FileHash -LiteralPath $bridgeDll -Algorithm SHA256).Hash; use_installed=[bool]$UseInstalled } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'session.json') -Encoding UTF8
    Write-Output "Started isolated Revit 2024 build 24.0.4.427, PID $($process.Id), language $Language."
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $last = ''
    $probed = $false
    $probeError = $null
    while (!$process.HasExited -and $watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $reportFile = Join-Path $run 'report.json'
        if (Test-Path -LiteralPath $reportFile) {
            try {
                $report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
                $progress = "$($report.state): $(@($report.cases).Count) cases; last=$(@($report.cases)[-1].name) $(@($report.cases)[-1].state)"
                if ($progress -ne $last) { Write-Output $progress; $last = $progress }
                if ($ProbeServer -and $report.finished_utc -and !$probed) {
                    $probed = $true
                    try {
                        $healthPath = Join-Path $run 'mcp-health.json'
                        & (Join-Path $repo 'scripts/hz-call.ps1') -Server $ProbeServer -Tool horizun_health -ArgumentsObject @{ include_capabilities=$true } -Json $healthPath -Quiet -TimeoutSec 90
                        if ($LASTEXITCODE -ne 0) { throw "MCP health failed; inspect $healthPath" }
                        $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
                        if (!$health.replied -or $health.is_error) { throw 'MCP health did not succeed.' }
                        & (Join-Path $repo 'scripts/hz-call.ps1') -Server $ProbeServer -Tool horizun_revit2024 -ArgumentsObject @{ operation='catalog'; target_document=(Join-Path $run 'electrical.rvt') } -Json (Join-Path $run 'mcp-catalog.json') -Quiet -TimeoutSec 90
                        if ($LASTEXITCODE -ne 0) { throw 'MCP catalog failed.' }
                        if ($report.state -eq 'passed') {
                            $arguments = @{ operation='analytical_member_create'; units='feet'; target_document=(Join-Path $run 'electrical.rvt'); points=@(@(0,150,0), @(10,150,0)) }
                            $previewPath = Join-Path $run 'mcp-preview.json'
                            & (Join-Path $repo 'scripts/hz-call.ps1') -Server $ProbeServer -Tool horizun_revit2024 -ArgumentsObject $arguments -Json $previewPath -Quiet -TimeoutSec 90
                            if ($LASTEXITCODE -ne 0) { throw 'MCP preview failed.' }
                            $preview = Get-Content -LiteralPath $previewPath -Raw | ConvertFrom-Json
                            $arguments.confirmation_token = $preview.result.confirmation_token
                            $arguments.dry_run = $false
                            $arguments.idempotency_key = 'audit-' + [Guid]::NewGuid().ToString('N')
                            $applyPath = Join-Path $run 'mcp-apply.json'
                            & (Join-Path $repo 'scripts/hz-call.ps1') -Server $ProbeServer -Tool horizun_revit2024 -ArgumentsObject $arguments -Json $applyPath -Quiet -TimeoutSec 90
                            if ($LASTEXITCODE -ne 0) { throw 'MCP apply failed.' }
                            $applied = Get-Content -LiteralPath $applyPath -Raw | ConvertFrom-Json
                            if ($applied.result.application.state -ne 'verified_applied') { throw 'MCP apply was not verified.' }
                            $replayPath = Join-Path $run 'mcp-replay.json'
                            & (Join-Path $repo 'scripts/hz-call.ps1') -Server $ProbeServer -Tool horizun_revit2024 -ArgumentsObject $arguments -Json $replayPath -Quiet -TimeoutSec 90
                            if ($LASTEXITCODE -ne 0) { throw 'MCP idempotency replay failed.' }
                            $replayed = Get-Content -LiteralPath $replayPath -Raw | ConvertFrom-Json
                            if ($replayed.result.result.element_id -ne $applied.result.result.element_id) { throw 'Replay returned a different element.' }
                        }
                        Write-Output 'Fresh MCP server: health, catalog and eligible write/replay checks completed over stdio/pipe.'
                    } catch { $probeError = $_.Exception.Message; Write-Output $probeError }
                    finally { 'done' | Set-Content -LiteralPath (Join-Path $run 'mcp-complete') }
                }
                if ($KeepOpen -and !$ProbeServer -and $report.finished_utc) { break }
            } catch { } # writer may be replacing the report
        }
        Start-Sleep -Seconds 3
        $process.Refresh()
    }
    if (!$process.HasExited) { Write-Output 'Revit is still running. No process was killed and manifests remain in audit state until normal exit.'; exit 2 }
    $reportFile = Join-Path $run 'report.json'
    if (!(Test-Path -LiteralPath $reportFile)) { throw 'Revit exited without a test report.' }
    $report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
    Write-Output "Report: $reportFile ($($report.state))"
    if ($report.state -ne 'passed') { exit 1 }
    if ($probeError) { throw $probeError }
} finally {
    Remove-Item Env:HORIZUN_AUDIT_RUN_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:HORIZUN_AUDIT_KEEP_OPEN -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.HasExited) {
        if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
        if ($moved) {
            if (Test-Path -LiteralPath $installed) { throw 'Manifest restore conflict; the original is preserved in the aside file.' }
            Move-Item -LiteralPath $aside -Destination $installed
            if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $snapshot) { throw 'Restored manifest hash differs.' }
        }
        Write-Output 'Original add-in manifest restored. Installed binaries were not replaced.'
    }
}
