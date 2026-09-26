#Requires -Version 5.1
# Exercises pipe-slope.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'pipe-slope.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'pipe-slope' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }

function New-Fakes([double[]]$slopes, [bool]$loseConnector) {
    $script:nextId = 5000; $script:sent = @{}; $script:deleted = $null; $script:queried = 0
    $script:slopes = $slopes; $script:lose = $loseConnector
}
$script:fakeCall = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_list_elements') { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 30 }) }) $false '' }
        if ($tool -eq 'horizun_query_model' -and $arguments.include_types) {
            $row = switch ($arguments.categories[0]) { 'OST_PipeCurves' { 31 } 'OST_PipingSystem' { 32 } default { $null } }
            $rows = if ($row) { @([pscustomobject]@{ element_id = $row; is_element_type = $true }) } else { @() }
            return & $reply ([pscustomobject]@{ rows = $rows }) $false ''
        }
        if ($tool -eq 'horizun_query_model' -and $arguments.include_mep) {
            $script:queried++
            $rows = foreach ($id in $arguments.element_ids) {
                $n = if ($script:lose -and $script:queried -gt 1 -and $id -eq 5002) { 1 } else { 2 }
                [pscustomobject]@{ element_id = $id; connectors = @(1..$n | ForEach-Object { [pscustomobject]@{ is_connected = $true } }) }
            }
            return & $reply ([pscustomobject]@{ rows = @($rows) }) $false ''
        }
        return & $reply $null $true "unexpected call $tool"
}
$script:fakeApply = {
        param($tool, $arguments, $key)
        $script:sent[$key] = $arguments
        if ($tool -eq 'horizun_create_elements') {
            $script:nextId++
            return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) }) $false '') }
        }
        if ($tool -eq 'horizun_mep_routing') {
            $i = 0
            $rows = foreach ($id in $arguments.element_ids) {
                $s = $script:slopes[$i]; $i++
                [pscustomobject]@{ element_id = $id; start_elevation = 3000.0; end_elevation = 3000.0 - 60.0; slope_percent = $s }
            }
            $data = [pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
                result = [pscustomobject]@{ pipes = @($rows); reconnected = @() } }
            return @{ stage = 'apply'; answer = (& $reply $data $false '') }
        }
        if ($tool -eq 'horizun_delete_verified') { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{}) $false '') } }
        return @{ stage = 'apply'; answer = (& $reply $null $true "unexpected apply $tool") }
}

# 1: the happy path.
New-Fakes @(2.0, 2.01, 1.99) $false
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $script:fakeCall; Apply = $script:fakeApply }
$cases = @(& $module.Run $ctx)
Check 'four cases, all pass' ($cases.Count -eq 4 -and @($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
$slopeArgs = $script:sent['t1-ps-slope']
Check 'slope sent to the three pipes, 2 percent, first pipe held high' ($slopeArgs.operation -eq 'slope' -and @($slopeArgs.element_ids).Count -eq 3 -and $slopeArgs.slope_percent -eq 2.0 -and $slopeArgs.fixed_end -eq '5001:high')
Check 'elbows join pipe k and k+1' ($script:sent['t1-ps-elbow0'].elements[0].fitting -eq 'elbow' -and $script:sent['t1-ps-elbow1'].elements[0].elements[1].element_id -eq 5003)
Check 'cleanup deletes the five created ids, elbows first' (@($script:deleted).Count -eq 5 -and @($script:deleted)[0] -eq 5005)

# 2: a pipe re-reading 2.1 percent fails case 2.
New-Fakes @(2.0, 2.1, 2.0) $false
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't2'; WriteGate = $false; Call = $script:fakeCall; Apply = $script:fakeApply }
$cases = @(& $module.Run $ctx)
Check 'slope off by 0.1 pp fails' ($cases[1].Outcome -eq 'fail' -and $cases[1].Detail -match '2.1')

# 3: a connector lost after the slope fails case 3.
New-Fakes @(2.0, 2.0, 2.0) $true
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't3'; WriteGate = $false; Call = $script:fakeCall; Apply = $script:fakeApply }
$cases = @(& $module.Run $ctx)
Check 'lost connector fails the independent re-read' ($cases[2].Outcome -eq 'fail' -and $cases[2].Detail -match '5002')

# 4: write tier closed -> every case not_covered, nothing called.
New-Fakes @(2.0, 2.0, 2.0) $false
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't4'; WriteGate = $true; Call = $script:fakeCall; Apply = $script:fakeApply }
$cases = @(& $module.Run $ctx)
Check 'write gate closed: four not_covered, no apply' ($cases.Count -eq 4 -and @($cases | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $script:sent.Count -eq 0)

if ($fails -gt 0) { "pipe-slope.tests: $fails failure(s)"; exit 1 }
'pipe-slope.tests: all passed'
