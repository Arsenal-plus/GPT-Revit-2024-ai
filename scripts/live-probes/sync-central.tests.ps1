#Requires -Version 5.1
# Exercises sync-central.probes.ps1 WITHOUT Revit. Shapes from the code
# (DocumentSessionSync.cs: SyncRefuse detail.code, the estimate preview, the apply
# report), to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'sync-central.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'sync-central' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('hz-sync-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$writePath = Join-Path $dir 'HZ_WRITE.rvt'; Set-Content -LiteralPath $writePath -Value 'x'
Set-Content -LiteralPath (Join-Path $dir 'HZ_CLOSED.rvt') -Value 'x'

function Ok($data) { [pscustomobject]@{ isError = $false; data = [pscustomobject]$data; text = ''; structured = $null } }
function Refused($code, $text) { [pscustomobject]@{ isError = $true; data = [pscustomobject]@{ code = $code; write_started = $false }; text = $text; structured = $null } }

$script:mode = 'python_off'
$script:calls = New-Object System.Collections.ArrayList
$call = {
    param($tool, $a)
    [void]$script:calls.Add(($tool + ':' + [string]$a.operation + ':' + [string]$a.target_document))
    if ($tool -eq 'horizun_health') { return (Ok @{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $writePath }) }) }
    if ($tool -eq 'horizun_open_document') { return (Ok @{ title = 'HZ_WRITE' }) }
    if ($tool -eq 'horizun_execute_python') {
        if ($script:mode -eq 'python_off') { return (Refused 'tool_disabled' 'horizun_execute_python is DISABLED ON THIS MACHINE') }
        return (Ok @{ __output__ = [pscustomobject]@{ central = 'C:\t\HZ_SYNC_CENTRAL.rvt'; local = 'C:\t\HZ_SYNC_LOCAL.rvt'; central_title = 'HZ_SYNC_CENTRAL'; local_exists = $true } })
    }
    switch ([string]$a.operation) {
        'open' {
            if ([string]$a.file_path -like '*HZ_CLOSED*') { return (Ok @{ title = 'HZ_CLOSED_detached' }) }
            return (Ok @{ title = 'HZ_SYNC_LOCAL' })
        }
        'close' { return (Ok @{ would_discard_unsaved = $false; closed = $true }) }
        'sync_with_central' {
            if ($a.target_document -eq 'HZ_WRITE') { return (Refused 'not_workshared' "'HZ_WRITE' is not workshared") }
            if ($a.target_document -eq 'HZ_CLOSED_detached') { return (Refused 'detached_copy' "'HZ_CLOSED_detached' is a DETACHED copy") }
            if ($script:mode -eq 'owner_off') { return (Refused 'sync_not_authorised' 'Synchronize with central is OFF ... Horizun Hub tab > Advanced options > Synchronize with central.') }
            if ($a.dry_run) {
                return (Ok @{ operation = 'sync_with_central'; dry_run = $true; preview_kind = 'estimate'; owned_elements = 3; borrowed_elements = 1
                        update_status_sample = [pscustomobject]@{ sample_size = 12 }; confirmation_token = 'tok' })
            }
            if ($a.confirmation_token -ne 'tok') { return (Refused 'confirmation_rejected' 'bad token') }
            return (Ok @{ operation = 'sync_with_central'; dry_run = $false; sync_verified = $true
                    ownership = [pscustomobject]@{ verified = $true; owned_worksets_after = 0; owned_elements_after = 0 } })
        }
    }
    return (Ok @{})
}
function Ctx($writeGate) {
    [pscustomobject]@{ Call = $call; Apply = $null; Document = 'HZ_WRITE'; RunId = 'r1'; Year = '2026'; WriteGate = $writeGate; ClosedWorksetDocument = 'HZ_CLOSED' }
}
function Outcome($cases, $prefix) { (@($cases | Where-Object { $_.Name -like ($prefix + '*') }) | Select-Object -First 1).Outcome }

try {
    $c = & $module.Run (Ctx $true)
    Check 'write gate closed: every case not_covered' ((@($c | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0) -and $c.Count -eq 4)

    $script:mode = 'python_off'
    $c = & $module.Run (Ctx $false)
    Check 'not_workshared refusal passes' ((Outcome $c 'sync central: the non-workshared') -eq 'pass')
    Check 'detached copy refusal passes' ((Outcome $c 'sync central: a detached') -eq 'pass')
    Check 'no python: owner-off case is not_covered' ((Outcome $c 'sync central: with the owner') -eq 'not_covered')
    Check 'no python: real sync is not_covered, never forced' ((Outcome $c 'sync central: preview is') -eq 'not_covered')
    Check 'the detached fixture is closed afterwards' (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_CLOSED_detached' }).Count -ge 1)

    $script:mode = 'owner_off'; $script:calls.Clear()
    $c = & $module.Run (Ctx $false)
    Check 'owner off: refusal naming Advanced options passes' ((Outcome $c 'sync central: with the owner') -eq 'pass')
    Check 'owner off: real sync not_covered' ((Outcome $c 'sync central: preview is') -eq 'not_covered')
    Check 'owner off: the scratch local and the renamed central are both closed' (
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_SYNC_LOCAL' }).Count -ge 1) -and
        (@($script:calls | Where-Object { $_ -eq 'horizun_document_session:close:HZ_SYNC_CENTRAL' }).Count -ge 1))

    $script:mode = 'owner_on'
    $c = & $module.Run (Ctx $false)
    Check 'owner on: estimate then verified sync passes' ((Outcome $c 'sync central: preview is') -eq 'pass')
    Check 'owner on: owner-off case is not_covered' ((Outcome $c 'sync central: with the owner') -eq 'not_covered')
    Check 'every catalog name is reported' ((@($c | ForEach-Object { $_.Name }) | Sort-Object) -join '|' -eq ((@($module.Catalog | ForEach-Object { $_.Name }) | Sort-Object) -join '|'))
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }

if ($fails -gt 0) { "sync-central.tests: $fails FAILED"; exit 1 } else { 'sync-central.tests: all passed'; exit 0 }
