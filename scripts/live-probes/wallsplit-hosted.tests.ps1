#Requires -Version 5.1
# Exercises wallsplit-hosted.probes.ps1 WITHOUT Revit. The fakes copy the REAL reply shapes
# MEASURED 2026-09-26 in Revit 2026: the split rehearsal answers eligible[].wall_id +
# layer_plan[] + confirmation_token; the apply answers walls[].source_wall_id/applied/code/
# layers[].resulting_wall_id/is_core_carrier, all_verified, walls_with_cut_proof and a
# spatial_check whose findings carry severity/reason/a.id/b.id; horizun_clash answers
# clashes[].a.element_id/b.element_id and pairs_tested.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'wallsplit-hosted.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'wallsplit-hosted' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

# $mode: ok | blocked-error | unreported | host-moved | no-types
function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 500; Ids = @{}; Deleted = $null; Calls = New-Object System.Collections.ArrayList }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        [void]$s.Calls.Add($tool)
        switch ($tool) {
            'horizun_query_model' {
                if ($a.element_ids) {
                    $rows = @($a.element_ids | ForEach-Object {
                        $hostId = if ($s.Mode -eq 'host-moved' -and $s.Split -and $_ -eq $s.Ids['window']) { 99 } else { $s.Ids['wall'] }
                        [pscustomobject]@{ element_id = $_; unique_id = "uid-$_"; host_id = $hostId
                            parameters = [pscustomobject]@{ INSTANCE_SILL_HEIGHT_PARAM = [pscustomobject]@{ raw = 2.95 }; INSTANCE_HEAD_HEIGHT_PARAM = [pscustomobject]@{ raw = 6.95 } } } })
                    return & $reply ([pscustomobject]@{ rows = $rows })
                }
                if ($s.Mode -eq 'no-types') { return & $reply ([pscustomobject]@{ rows = @() }) }
                $row = switch (@($a.categories)[0]) {
                    'OST_Walls' { [pscustomobject]@{ element_id = 11; is_element_type = $true; family = 'Basic Wall'; type = 'Exterior - Brick on Mtl. Stud'; name = 'Exterior - Brick on Mtl. Stud' } }
                    'OST_Doors' { [pscustomobject]@{ element_id = 12; is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm'; name = '0915 x 2134mm' } }
                    'OST_Windows' { [pscustomobject]@{ element_id = 13; is_element_type = $true; family = 'M_Fixed'; type = '0915 x 1220mm'; name = '0915 x 1220mm' } }
                }
                return & $reply ([pscustomobject]@{ rows = @($row) })
            }
            'horizun_split_multilayer_walls' {
                $plan = [pscustomobject]@{ wall_id = $s.Ids['wall']; layer_plan = @(1..7 | ForEach-Object { [pscustomobject]@{ layer_index = $_ } }) }
                return & $reply ([pscustomobject]@{ dry_run = $true; eligible = @($plan); confirmation_token = 'hz-tok' })
            }
            'horizun_clash' {
                # volumes MEASURED 2026-09-26: plywood 7.7 L, air 2.4 L (under the 3 L frame-touch threshold), gypsum 5.1 L
                $vols = @{ 601 = 0.002412; 602 = 0.007724; 603 = 0.005149 }
                $clashes = @(foreach ($l in $s.Layers[1..3]) { [pscustomobject]@{ a = [pscustomobject]@{ element_id = [string]$s.Ids['door']; category = 'Doors' }; b = [pscustomobject]@{ element_id = [string]$l; category = 'Walls' }; intersection_volume_m3 = $vols[$l] } })
                return & $reply ([pscustomobject]@{ clashes = $clashes; pairs_tested = 20 })
            }
        }
        return & $reply $null $true 'unexpected call'
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        [void]$s.Calls.Add('apply:' + $tool)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_copy_between_documents' { return @{ stage = 'dry_run'; answer = (& $reply $null $true 'no template in the fake') } }
            'horizun_create_elements' {
                $id = $s.Next; $s.Next++
                foreach ($k in 'level', 'wall', 'door', 'window') { if ($key -like "*-wsh-$k") { $s.Ids[$k] = $id } }
                return & $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }); application = [pscustomobject]@{ state = 'verified_applied' } })
            }
            'horizun_split_multilayer_walls' {
                $s.Split = $true
                $s.Layers = @(600, 601, 602, 603)
                $layers = @($s.Layers | ForEach-Object { [pscustomobject]@{ resulting_wall_id = $_; is_core_carrier = $false } }) + @([pscustomobject]@{ resulting_wall_id = $s.Ids['wall']; is_core_carrier = $true })
                $row = [pscustomobject]@{ source_wall_id = $s.Ids['wall']; applied = $true; code = $null; message = 'converted and verified'; layers = $layers }
                $findings = @(foreach ($l in $s.Layers[1..3]) {
                    if ($l -eq 601) { continue }   # 2.4 L: the spatial check calls it expected, as measured
                    if ($s.Mode -eq 'unreported' -and $l -eq 603) { continue }
                    $sev = if ($s.Mode -eq 'blocked-error') { 'error' } else { 'warning' }
                    $why = if ($s.Mode -eq 'blocked-error') { 'door is blocked by wall' } else { "the door's frame or trim extends into a wall that lines its host (a layer of a split compound wall, or a lining); the opening itself is cut through the host" }
                    [pscustomobject]@{ kind = 'conflict'; severity = $sev; reason = $why; a = [pscustomobject]@{ id = $l; category = 'Walls' }; b = [pscustomobject]@{ id = $s.Ids['door']; category = 'Doors' } } })
                return & $ok ([pscustomobject]@{ walls = @($row); all_verified = $true; walls_with_cut_proof = 1
                    spatial_check = [pscustomobject]@{ status = 'conflicts'; findings = $findings } })
            }
            'horizun_delete_verified' { $s.Deleted = @($a.ids); return & $ok ([pscustomobject]@{ deleted_total = @($a.ids).Count }) }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true 'unexpected apply') }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; TemplateRoot = 'C:\no-such-template-root'; Call = $call; Apply = $apply } }
}
function Run($f) { $h = @{}; foreach ($c in @(& $module.Run $f.Ctx)) { $h[$c.Name] = $c }; $h }
$n = @($module.Catalog | ForEach-Object { $_.Name })

$f = New-Fake 'ok'; $r = Run $f
Check 'every catalogued case is reported' (@($n | Where-Object { -not $r.ContainsKey($_) }).Count -eq 0)
Check ('everything passes when every touch is a lining warning (' + ((@($n | Where-Object { $r[$_].Outcome -ne 'pass' })) -join '; ') + ')') (@($n | Where-Object { $r[$_].Outcome -ne 'pass' }).Count -eq 0)
Check 'the inserts, the layer walls, the carrier and then the level are deleted, level last' ((@($f.State.Deleted)[-1] -eq $f.State.Ids['level']) -and (@($f.State.Deleted) -contains 600) -and (@($f.State.Deleted)[0] -eq $f.State.Ids['door']))

$f = New-Fake 'blocked-error'; $r = Run $f
Check 'a touch reported as an error ("blocked") fails the lining case' ($r[$n[3]].Outcome -eq 'fail' -and $r[$n[3]].Detail -match 'errors')

$f = New-Fake 'unreported'; $r = Run $f
Check 'a touch under 3 L may go unreported (expected frame touch), one above it may not' ($r[$n[3]].Outcome -eq 'fail' -and $r[$n[3]].Detail -match '603')

$f = New-Fake 'host-moved'; $r = Run $f
Check 'an insert that changed host fails the insert case' ($r[$n[2]].Outcome -eq 'fail' -and $r[$n[2]].Detail -match 'not the carrier')

$f = New-Fake 'no-types'; $r = Run $f
Check 'without the types and without a template the measured cases are not_covered, never a pass' (@($n[0..3] | Where-Object { $r[$_].Outcome -ne 'not_covered' }).Count -eq 0)
Check 'and the level it created is still deleted' ($r[$n[4]].Outcome -eq 'pass' -and @($f.State.Deleted) -contains $f.State.Ids['level'])

$f = New-Fake 'ok'; $f.Ctx.WriteGate = $true; $r = Run $f
Check 'a closed write tier reports every case not_covered and calls nothing' ((@($n | Where-Object { $r[$_].Outcome -ne 'not_covered' }).Count -eq 0) -and $f.State.Calls.Count -eq 0)

if ($fails) { "wallsplit-hosted tests: $fails FAILED"; exit 1 } else { 'wallsplit-hosted tests: ALL PASS'; exit 0 }
