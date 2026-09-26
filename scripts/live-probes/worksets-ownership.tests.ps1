#Requires -Version 5.1
# Exercises worksets-ownership.probes.ps1 WITHOUT Revit.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'worksets-ownership.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'worksets-ownership' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$workshared, [bool]$hasWall = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; workshared = $workshared }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_list_elements') {
            $rows = if ($hasWall) { @([pscustomobject]@{ element_id = 501; source_kind = 'host' }) } else { @() }
            return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
        }
        if ($tool -eq 'horizun_manage_worksets') {
            if (-not $state.workshared) { return @{ isError = $true; text = 'not workshared'; data = [pscustomobject]@{ code = 'not_workshared' } } }
            return @{ isError = $false; data = [pscustomobject]@{ worksets = @([pscustomobject]@{ workset_id = 0; name = 'Workset1' }) } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        $effect = [pscustomobject]@{ measured = $true; elements_examined = 0; elements_newly_owned_by_me = 0 }
        $data = [pscustomobject]@{
            workset_id       = 42
            ownership_effect = $effect
        }
        if ($key -eq 'own-rename') {
            $data | Add-Member relinquish_after ([pscustomobject]@{ attempted = $true; elements_still_owned_by_me = 0 })
        }
        $dry = @{ data = [pscustomobject]@{ plan = [pscustomobject]@{ move = @([pscustomobject]@{ from_workset_id = 0 }) } } }
        return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' }; dry = $dry }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

# A. write tier closed
$by = Run-Module (New-Ctx $true $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

# B. not workshared
$by = Run-Module (New-Ctx $false $false)
Check 'not workshared: every case not_covered with the reason' (
    (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0) -and
    ($by[$catalog[0]].Detail -match 'not workshared'))

# C. workshared, a free wall available
$ctx = New-Ctx $false $true $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
Check 'create reports a measured ownership_effect' ($by[$catalog[0]].Outcome -eq 'pass')
Check 'rename reports ownership_effect and relinquish_after' ($by[$catalog[1]].Outcome -eq 'pass' -and $by[$catalog[1]].Detail -match 'relinquish')
Check 'move_elements reports ownership_effect' ($by[$catalog[2]].Outcome -eq 'pass')
Check 'a moved element is moved back' ($ctx.State.applies.Contains('own-move-back'))

# D. workshared, no free wall
$by = Run-Module (New-Ctx $false $true $false)
Check 'move_elements is not_covered without a free wall' ($by[$catalog[2]].Outcome -eq 'not_covered')

# E. write model not workshared, but the run names a closed-workset fixture: it is
# opened DETACHED, the three cases run on it, and it is closed without saving.
$fxDir = Join-Path $env:TEMP ('hz-own-fx-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fxDir | Out-Null
Set-Content -LiteralPath (Join-Path $fxDir 'HZ_WRITE.rvt') -Value 'rvt' -Encoding ascii
Set-Content -LiteralPath (Join-Path $fxDir 'HZ_CLOSED_L.rvt') -Value 'rvt' -Encoding ascii
$fxState = @{ calls = New-Object System.Collections.Generic.List[string]; applies = New-Object System.Collections.Generic.List[string] }
$fxCall = {
    param($tool, $arguments)
    $fxState.calls.Add($tool + ':' + [string]$arguments.operation + ':' + [string]$arguments.target_document)
    switch ($tool) {
        'horizun_health' { return @{ isError = $false; data = [pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = (Join-Path $fxDir 'HZ_WRITE.rvt') }) } } }
        'horizun_document_session' {
            if ($arguments.operation -eq 'open') { return @{ isError = $false; data = [pscustomobject]@{ title = 'HZ_CLOSED_L_detached' } } }
            if ($arguments.operation -eq 'close' -and $arguments.dry_run) { return @{ isError = $false; data = [pscustomobject]@{ confirmation_token = 'tok' } } }
            if ($arguments.operation -eq 'close') { return @{ isError = $false; data = [pscustomobject]@{ closed = $true } } }
        }
        'horizun_open_document' { return @{ isError = $false; data = [pscustomobject]@{ confirmed_active = $true } } }
        'horizun_list_elements' {
            if ($arguments.category -eq 'OST_DuctCurves') { return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 777; source_kind = 'host' }) } } }
            return @{ isError = $false; data = [pscustomobject]@{ rows = @() } }
        }
        'horizun_manage_worksets' {
            if ($arguments.target_document -eq 'HZ_CLOSED_L_detached') { return @{ isError = $false; data = [pscustomobject]@{ worksets = @([pscustomobject]@{ workset_id = 0 }) } } }
            return @{ isError = $true; text = 'not workshared'; data = [pscustomobject]@{ code = 'not_workshared' } }
        }
    }
    return @{ isError = $true; text = 'unexpected tool ' + $tool }
}.GetNewClosure()
$fxApply = {
    param($tool, $arguments, $key)
    $fxState.applies.Add($key + '@' + [string]$arguments.target_document)
    $data = [pscustomobject]@{ workset_id = 42; ownership_effect = [pscustomobject]@{ measured = $true; elements_examined = 1; elements_newly_owned_by_me = 0 } }
    if ($key -eq 'own-rename') { $data | Add-Member relinquish_after ([pscustomobject]@{ attempted = $true; elements_still_owned_by_me = 0 }) }
    $dry = @{ data = [pscustomobject]@{ plan = [pscustomobject]@{ move = @([pscustomobject]@{ from_workset_id = 0 }) } } }
    return @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' }; dry = $dry }
}.GetNewClosure()
$fxCtx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r2'; WriteGate = $false; ClosedWorksetDocument = 'HZ_CLOSED_L'; Call = $fxCall; Apply = $fxApply }
$by = Run-Module $fxCtx
Check 'fixture: all three cases pass on the detached workshared copy' (@($catalog | Where-Object { $by[$_].Outcome -ne 'pass' }).Count -eq 0)
Check 'fixture: every write targeted the detached copy, not the write model' (@($fxState.applies | Where-Object { $_ -notlike '*@HZ_CLOSED_L_detached' }).Count -eq 0)
Check 'fixture: the write model is re-activated and the copy closed' ($fxState.calls.Contains('horizun_open_document::') -and $fxState.calls.Contains('horizun_document_session:close:HZ_CLOSED_L_detached'))
Remove-Item -LiteralPath $fxDir -Recurse -Force -ErrorAction SilentlyContinue
if ($fails) { "worksets-ownership probe tests: $fails FAILED"; exit 1 } else { 'worksets-ownership probe tests: ALL PASS'; exit 0 }
