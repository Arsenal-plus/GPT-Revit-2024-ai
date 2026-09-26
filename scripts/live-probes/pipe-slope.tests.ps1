#Requires -Version 5.1
# Exercises pipe-slope.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The fake query_model reply has the shape the server sends: connectors under
# row.mep (QueryModelCommand, include_mep), each with is_connected and origin in mm.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'pipe-slope.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'pipe-slope' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }

# slopes: per-pipe slope_percent; lose: 5002 loses a connector; heldStart: first pipe's start;
# reverse: pipe 2 rises toward the outlet; offMm: elbow 5005's second connector this far off its pipe end.
function New-Fakes([double[]]$slopes, [bool]$loseConnector, [double]$heldStart = 3000.0, [bool]$reverse = $false, [double]$offMm = 0.0) {
    $script:nextId = 5000; $script:sent = @{}; $script:deleted = $null; $script:queried = 0
    $script:slopes = $slopes; $script:lose = $loseConnector; $script:heldStart = $heldStart; $script:reverse = $reverse; $script:offMm = $offMm
}
function Fake-Connector($id, $x, $y, $connected) { [pscustomobject]@{ id = $id; is_connected = $connected; origin = @([double]$x, [double]$y, 3000.0) } }
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
            $after = $script:queried -gt 1
            $rows = foreach ($id in $arguments.element_ids) {
                $conns = if ($id -le 5003) {
                    $k = $id - 5001
                    $second = -not ($script:lose -and $after -and $id -eq 5002)
                    @((Fake-Connector 1 ($k * 1000) 0 $true), (Fake-Connector 2 (($k + 1) * 1000) 0 $second))
                }
                else {
                    $x = if ($id -eq 5004) { 1000 } else { 2000 }
                    $dy = if ($after -and $id -eq 5005) { $script:offMm } else { 0 }
                    @((Fake-Connector 1 $x 0 $true), (Fake-Connector 2 $x $dy $true))
                }
                [pscustomobject]@{ element_id = $id; mep = [pscustomobject]@{ connectors = @($conns); open_connectors = 0 } }
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
                $s = $script:slopes[$i]
                $start = if ($i -eq 0) { $script:heldStart } else { 3000.0 - 60.0 * $i }
                $end = $start - 60.0
                if ($script:reverse -and $i -eq 1) { $t = $start; $start = $end; $end = $t }
                $i++
                [pscustomobject]@{ element_id = $id; start_elevation = $start; end_elevation = $end; slope_percent = $s }
            }
            $data = [pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
                result = [pscustomobject]@{ pipes = @($rows); reconnected = @() } }
            return @{ stage = 'apply'; answer = (& $reply $data $false '') }
        }
        if ($tool -eq 'horizun_delete_verified') { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{}) $false '') } }
        return @{ stage = 'apply'; answer = (& $reply $null $true "unexpected apply $tool") }
}
function Run-Probe($runId, $gate = $false) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $gate; Call = $script:fakeCall; Apply = $script:fakeApply }
    return @(& $module.Run $ctx)
}

# 1: the happy path.
New-Fakes @(2.0, 2.01, 1.99) $false
$cases = Run-Probe 't1'
Check 'four cases, all pass' ($cases.Count -eq 4 -and @($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the connector case reports the elbow-to-pipe gap' ($cases[2].Detail -match 'worst elbow-to-pipe gap 0 mm')
$slopeArgs = $script:sent['t1-ps-slope']
Check 'slope sent to the three pipes, 2 percent, first pipe held high' ($slopeArgs.operation -eq 'slope' -and @($slopeArgs.element_ids).Count -eq 3 -and $slopeArgs.slope_percent -eq 2.0 -and $slopeArgs.fixed_end -eq '5001:high')
Check 'elbows join pipe k and k+1' ($script:sent['t1-ps-elbow0'].elements[0].fitting -eq 'elbow' -and $script:sent['t1-ps-elbow1'].elements[0].elements[1].element_id -eq 5003)
Check 'cleanup deletes the five created ids, elbows first' (@($script:deleted).Count -eq 5 -and @($script:deleted)[0] -eq 5005)

# 2: a pipe re-reading 2.1 percent fails case 2.
New-Fakes @(2.0, 2.1, 2.0) $false
$cases = Run-Probe 't2'
Check 'slope off by 0.1 pp fails' ($cases[1].Outcome -eq 'fail' -and $cases[1].Detail -match '2.1')

# 3: a connector lost after the slope fails case 3.
New-Fakes @(2.0, 2.0, 2.0) $true
$cases = Run-Probe 't3'
Check 'lost connector fails the independent re-read' ($cases[2].Outcome -eq 'fail' -and $cases[2].Detail -match '5002')

# 4: write tier closed -> every case not_covered, nothing called.
New-Fakes @(2.0, 2.0, 2.0) $false
$cases = Run-Probe 't4' $true
Check 'write gate closed: four not_covered, no apply' ($cases.Count -eq 4 -and @($cases | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $script:sent.Count -eq 0)

# 5: the held end moved 10 mm -> case 2 fails even with every slope right.
New-Fakes @(2.0, 2.0, 2.0) $false 3010.0
$cases = Run-Probe 't5'
Check 'held end moved fails' ($cases[1].Outcome -eq 'fail' -and $cases[1].Detail -match 'held end' -and $cases[1].Detail -match '3010')

# 6: pipe 2 drains backwards at the right magnitude -> case 2 fails.
New-Fakes @(2.0, 2.0, 2.0) $false 3000.0 $true
$cases = Run-Probe 't6'
Check 'a pipe rising toward the outlet fails' ($cases[1].Outcome -eq 'fail' -and $cases[1].Detail -match 'not falling' -and $cases[1].Detail -match '5002')

# 7: an elbow still flagged connected but 5 mm off its pipe end -> case 3 fails.
New-Fakes @(2.0, 2.0, 2.0) $false 3000.0 $false 5.0
$cases = Run-Probe 't7'
Check 'an elbow connector off its pipe end fails' ($cases[2].Outcome -eq 'fail' -and $cases[2].Detail -match '5005#2 5 mm')

if ($fails -gt 0) { "pipe-slope.tests: $fails failure(s)"; exit 1 }
'pipe-slope.tests: all passed'
