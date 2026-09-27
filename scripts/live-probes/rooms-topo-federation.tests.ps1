#Requires -Version 5.1
# Exercises rooms-topo-federation.probes.ps1 WITHOUT Revit. Shapes from the code
# (FederationLevelRules.cs / FederationCheckCommand.cs, CreateElementsEnclosed.cs /
# CreateElementsToposolid.cs), to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'rooms-topo-federation.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'rooms-topo-federation' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $src = Join-Path $env:TEMP ('hz-fake-host-' + [guid]::NewGuid().ToString('N') + '.rvt')
    Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $s = @{ Mode = $mode; Deleted = @(); Src = $src; Linked = $false; Rooms = $false; NextId = 5000; Made = @() }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $s.Src }) }) }
            'horizun_federation_check' {
                if ($a.rules.levels_match -is [hashtable] -and $a.rules.levels_match.ContainsKey('tol')) {
                    return & $reply $null $true "The federation rules were refused: levels_match: unknown key 'tol'. Known: tolerance_mm."
                }
                $rows = @(); $links = @()
                if ($s.Linked) {
                    $state = if ($s.Mode -eq 'differs') { 'differs' } else { 'matches' }
                    $matching = if ($s.Mode -eq 'differs') { 2 } else { 3 }
                    $rows += [pscustomobject]@{ instance_id = 901; title = 'HZ_LVLSRC'; state = $state; levels_compared = 3; levels_matching = $matching; mismatches = @(); host_levels_not_in_link = @() }
                    $links += [pscustomobject]@{ instance_id = 901; title = 'HZ_LVLSRC'; loaded = $true }
                }
                $verdict = if ($s.Mode -eq 'differs') { 'fails' } else { 'passes' }
                return & $reply ([pscustomobject]@{ verdict = $verdict; levels = $rows; links = $links; summary = [pscustomobject]@{ links_levels_differ = 0; links_levels_not_read = 0 } })
            }
            'horizun_query_model' {
                $cat = @($a.categories)[0]; $rows = @()
                if ($cat -eq 'OST_Walls') { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm'; element_id = 700 } }
                if ($cat -eq 'OST_Toposolid') {
                    if ($s.Mode -eq 'notopo') { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Terrain'; type = 'Site'; element_id = 711 } }
                    else { $rows += [pscustomobject]@{ is_element_type = $true; family = 'Toposolid'; type = 'Toposolid'; element_id = 710 } }
                }
                return & $reply ([pscustomobject]@{ rows = $rows })
            }
            'horizun_manage_phases' { return & $reply ([pscustomobject]@{ phases = @([pscustomobject]@{ index = 0; id = 11; name = 'Existing' }, [pscustomobject]@{ index = 1; id = 12; name = 'New Construction' }) }) }
            'horizun_create_elements' {
                # The rehearsal of an all_enclosed entry (ExpandEnclosed + NothingEnclosed shapes).
                $el = @($a.elements)[0]
                if ($el.kind -eq 'toposolid') { return & $reply $null $true 'elements[0]: toposolid_not_in_revit_2023: Revit 2023 has no Toposolid element (it arrived in Revit 2024). Nothing was planned.' }
                if ($el.phase_id -ne 12) { return & $reply $null $true 'phase_id must be the last phase' }
                $min = if ($el.ContainsKey('min_area_m2')) { [double]$el.min_area_m2 } else { 0 }
                $circuits = @()
                foreach ($c in @(@{ p = @(1153000, 2000); area = 22.04 }, @{ p = @(1157000, 2000); area = 6.84 })) {
                    $action = if ($el.kind -eq 'room' -and $s.Rooms) { 'skipped_has_room' } elseif ($c.area -lt $min) { 'skipped_min_area' } else { 'create' }
                    $circuits += [pscustomobject]@{ point_inside = $c.p; area_m2 = $c.area; sides = 4; is_room_located = [bool]$s.Rooms; action = $action }
                }
                $n = @($circuits | Where-Object { $_.action -eq 'create' }).Count
                $blk = [pscustomobject]@{ index = 0; kind = $el.kind; circuits = $circuits; circuits_seen = 2; to_create = $n
                    link_bounding = 'not_proven: whether Room Bounding walls of a LINKED model close a host circuit is not established by this build; measure it live.' }
                return & $reply ([pscustomobject]@{ dry_run = $true; requested = $n; enclosed = @($blk) })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_manage_links' { $s.Linked = $true; return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 }) }
            'horizun_delete_verified' { $s.Deleted += @($a.ids); if (@($a.ids) -contains 900) { $s.Linked = $false }; return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_copy_between_documents' { return & $ok ([pscustomobject]@{ copied = 0 }) }
            'horizun_create_elements' {
                $els = @($a.elements); $rows = @()
                $count = if ($els[0].placement -eq 'all_enclosed') { $s.Rooms = $true; 2 } else { $els.Count }
                for ($k = 0; $k -lt $count; $k++) { $s.NextId = $s.NextId + 1; $rows += [pscustomobject]@{ element_id = $s.NextId }; $s.Made += $s.NextId }
                return & $ok ([pscustomobject]@{ rows = $rows })
            }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $env:TEMP ('hz-rtf-' + [guid]::NewGuid().ToString('N'))); RunId = 't1'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($r) { ($r | ForEach-Object { $_.Outcome }) -join ',' }

$h = New-Fake 'ok'; $r = @(& $module.Run $h.Ctx)
Check ($r.Count -eq 12) ('twelve cases: ' + $r.Count)
Check (@($r | Where-Object { $_.Outcome -ne 'pass' -and $_.Name -notlike '*link-bounded*' }).Count -eq 0) ('everything but the link-bounded declaration passes: ' + (Outcomes $r))
Check ($r[9].Outcome -eq 'not_covered' -and $r[9].Detail -match 'not_proven') 'link-bounded circuits stay not_covered, with the reply''s declaration'
Check ($h.State.Deleted -contains 900) 'the probe link type is deleted'
Check (@($h.State.Made | Where-Object { $h.State.Deleted -notcontains $_ }).Count -eq 0 -and $h.State.Made.Count -eq 10) ('every staged id is deleted (2 levels, 5 walls, 2 rooms, 1 toposolid): ' + $h.State.Made.Count)

$h = New-Fake 'differs'; $r = @(& $module.Run $h.Ctx)
Check ($r[1].Outcome -eq 'fail' -and $r[2].Outcome -eq 'pass') 'a self link that differs fails the matches case but still answers'

$h = New-Fake 'ok'; $h.Ctx.Year = 2023; $r = @(& $module.Run $h.Ctx)
Check ($r[10].Outcome -eq 'pass' -and $r[10].Detail -match 'toposolid_not_in_revit_2023') ('2023 reports the named refusal: ' + $r[10].Outcome)

$h = New-Fake 'notopo'; $r = @(& $module.Run $h.Ctx)
Check ($r[10].Outcome -eq 'not_covered' -and $r[10].Detail -match 'Terrain: Site') ('no toposolid type by name: not_covered, naming what it saw: ' + $r[10].Detail)

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
Check ($r[1].Outcome -eq 'not_covered' -and $r[2].Outcome -eq 'not_covered' -and $r[3].Outcome -eq 'not_covered' -and $r[0].Outcome -eq 'pass') ('write tier closed, no links: ' + (Outcomes $r))
Check ($r.Count -eq 12 -and @($r[4..11] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0) 'write tier closed: rooms and toposolid cases are not_covered'

if ($fail -gt 0) { Write-Host "$fail check(s) failed"; exit 1 }
Write-Host 'all checks passed'
