#Requires -Version 5.1
# Exercises params-sequence.probes.ps1 WITHOUT Revit. The fake keeps the staged
# levels, walls and room, and answers write_params_verified 'sequence' the way
# WriteParamsSequence.cs / WriteParamsCommand.cs build their replies (data.sequence.order[]
# with target_id/value/level/room/room_from and repeats_across_levels on a rehearsal;
# rows[] with a string target_id, confirmed and value_read_back plus
# verification.verified on an apply; a refusal as isError text; a door's room_from
# 'to_room', as TargetRoom takes it through FamilyInstance.ToRoom) - shapes from the
# code, to be held against the first live run. Its ordering (elevation, x, y, id)
# is ParameterSequenceRules' for these keys, so the probe's hand-written expectation
# is checked against the rule, not against itself.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'params-sequence.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'params-sequence' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$lies = $false) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; el = @{}; next = 900; deleted = @(); written = @{} }
    $gen = {
        param($seq)
        $ob = @($seq.order_by)
        if ($ob -contains 'room' -and -not $seq.phase_id) {
            return @{ error = 'sequence.order_by room needs phase_id: a room exists in a phase. Nothing was written.' }
        }
        $items = @(foreach ($id in @($seq.element_ids)) {
            $e = $state.el[[long]$id]
            $room = if ($ob -contains 'room' -and @('room', 'door') -contains $e.kind -and [long]$seq.phase_id -eq 2) { '1' } else { $null }
            [pscustomobject]@{ id = [long]$id; kind = $e.kind; level = $e.level; elev = $e.elev; x = $e.x; y = $e.y; room = $room }
        })
        if ($ob -contains 'room' -and @($items | Where-Object { -not $_.room }).Count -gt 0) {
            return @{ error = 'sequence refused before anything was generated: 1 target(s) are not inside a room at the phase.' }
        }
        $pad = if ($seq.pad) { [int]$seq.pad } else { 0 }
        $n = 1; $last = $null; $seen = @{}; $rep = $false; $pos = 0
        $order = @(foreach ($t in @($items | Sort-Object elev, x, y, id)) {
            if ($seq.restart_per_level -and $pos -gt 0 -and $t.level -ne $last) { $n = 1 }
            $last = $t.level
            $digits = [string]$n; if ($pad -gt 0) { $digits = $digits.PadLeft($pad, '0') }
            $v = [string]$seq.prefix + $digits
            if ($seen.ContainsKey($v) -and $seen[$v] -ne $t.level) { $rep = $true }
            $seen[$v] = $t.level
            [pscustomobject]@{ position = $pos; target_id = $t.id; value = $v; level = $t.level; room = $t.room; room_from = $(if (-not $t.room) { $null } elseif ($t.kind -eq 'door') { 'to_room' } else { 'point' }) }
            $n++; $pos++
        })
        return @{ order = $order; rep = $rep }
    }.GetNewClosure()
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_model') {
            if (@($arguments.categories) -contains 'OST_Doors') {
                return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm'; element_id = 310 }) } }
            }
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm'; element_id = 300 }) } }
        }
        if ($tool -eq 'horizun_manage_phases') {
            return @{ isError = $false; data = [pscustomobject]@{ phases = @([pscustomobject]@{ id = 1; name = 'Existing' }, [pscustomobject]@{ id = 2; name = 'New Construction' }) } }
        }
        if ($tool -eq 'horizun_write_params_verified' -and $arguments.sequence) {
            $g = & $gen $arguments.sequence
            if ($g.error) { return @{ isError = $true; text = $g.error } }
            return @{ isError = $false; data = [pscustomobject]@{ mode = 'dry_run'; confirmation_token = 'tok'; sequence = [pscustomobject]@{ order = $g.order; repeats_across_levels = $g.rep } } }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        if ($tool -eq 'horizun_create_elements') {
            $x = $arguments.elements[0]; $state.next++; $id = $state.next
            switch ($x.kind) {
                'level' { $state.el[[long]$id] = @{ kind = 'level'; level = $x.name; elev = $x.elevation } }
                'wall' {
                    $lv = $state.el[[long]$x.level_id]
                    $state.el[[long]$id] = @{ kind = 'wall'; level = $lv.level; elev = $lv.elev; x = ($x.start[0] + $x.end[0]) / 2; y = ($x.start[1] + $x.end[1]) / 2 }
                }
                'room' { $lv = $state.el[[long]$x.level_id]; $state.el[[long]$id] = @{ kind = 'room'; level = $lv.level; elev = $lv.elev; x = $x.point[0]; y = $x.point[1] } }
                'family_instance' { $lv = $state.el[[long]$x.level_id]; $state.el[[long]$id] = @{ kind = 'door'; level = $lv.level; elev = $lv.elev; x = $x.point[0]; y = $x.point[1]; host = $x.host_id } }
            }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) } } }
        }
        if ($tool -eq 'horizun_write_params_verified') {
            $g = & $gen $arguments.sequence
            if ($g.error) { return @{ stage = 'dry_run'; answer = @{ isError = $true; text = $g.error } } }
            $rows = @(foreach ($o in $g.order) {
                $state.written[[string]$o.target_id] = $o.value
                $back = if ($lies) { 'something else' } else { $o.value }
                [pscustomobject]@{ target_id = [string]$o.target_id; confirmed = -not $lies; value_read_back = [pscustomobject]@{ storage = 'String'; value = $back } }
            })
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = $rows; verification = [pscustomobject]@{ verified = -not $lies } } } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected ' + $tool } }
    }.GetNewClosure()
    return [pscustomobject]@{ Document = 'HZ_WRITE'; RunId = 't1'; Year = 2026; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$failures = 0
function Expect($label, $cond) { if (-not $cond) { Write-Host "FAIL: $label"; $script:failures++ } else { Write-Host "ok: $label" } }

$ctx = New-Ctx $false
$r = @(& $module.Run $ctx)
Expect 'five cases' ($r.Count -eq 5)
Expect 'all pass against a truthful fake' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
$r | Where-Object { $_.Outcome -ne 'pass' } | ForEach-Object { Write-Host ("  " + $_.Outcome + ': ' + $_.Name + ' :: ' + $_.Detail) }
Expect 'the left wall of the box took 01 and level B restarted' ($ctx.State.written['906'] -eq 'HZSQt1-01' -and $ctx.State.written['907'] -eq 'HZSQt1-01' -and $ctx.State.written['904'] -eq 'HZSQt1-04')
Expect 'the room was numbered at the phase that holds it' ($ctx.State.written['909'] -eq 'HZSQt1-R1')
Expect 'the own door took its room through to_room and its Mark re-read' ($ctx.State.written['910'] -eq 'HZSQt1-D1')
Expect 'every staged element is deleted (2 levels, 6 walls, 1 room, 1 door; the existing types are kept)' (@($ctx.State.deleted).Count -eq 10 -and @($ctx.State.deleted) -notcontains 300 -and @($ctx.State.deleted) -notcontains 310)

$ctx = New-Ctx $false $true
$r = @(& $module.Run $ctx)
Expect 'a value that does not re-read fails the apply case' ($r[1].Outcome -eq 'fail')
Expect 'and the room case' ($r[3].Outcome -eq 'fail')
Expect 'and the door case' ($r[4].Outcome -eq 'fail')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' (@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all params-sequence probe tests passed'
