#Requires -Version 5.1
# Exercises docs-dry-run.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a model that
# answers the way the fixed tools do, and modes that answer the way the defects did.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'docs-dry-run.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'docs-dry-run' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Deleted = @() }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' }) }) }
            'horizun_verify_changes' {
                $blank = $s.Mode -eq 'blank'
                $img = [pscustomobject]@{ captured = (-not $blank); temporary_view_rollback = 'RolledBack'
                    content = [pscustomobject]@{ measured = $true; blank = $blank; content_pixels = $(if ($blank) { 0 } else { 9000 }) } }
                return & $reply ([pscustomobject]@{ status = 'ok'; image = $img })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($data) @{ stage = 'apply'; answer = (& $reply $data) } }
        switch ($tool) {
            'horizun_delete_verified' { $s.Deleted = @($a.ids); return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_create_elements' {
                $rows = @($a.elements | ForEach-Object { $id = $s.Next; $s.Next++; [pscustomobject]@{ element_id = $id } })
                return & $ok ([pscustomobject]@{ rows = $rows })
            }
            'horizun_create_schedule' {
                $sort = if ($s.Mode -eq 'groups-by-length') { @('Type', 'Length', 'Area', 'Volume') } else { @('Type') }
                $totals = if ($s.Mode -eq 'groups-by-length') { @() } else { @('Length', 'Area', 'Volume') }
                $id = $s.Next; $s.Next++
                return & $ok ([pscustomobject]@{ schedule_id = $id; body_rows = 9; grouping = [pscustomobject]@{ source = 'derived'; sort_group = $sort; totals = $totals }
                    postcondition = [pscustomobject]@{ all_verified = $true; properties = @() } })
            }
            'horizun_manage_views' {
                $rows = @()
                foreach ($act in $a.actions) {
                    $id = $s.Next; $s.Next++
                    $row = [pscustomobject]@{ operation = $act.operation; element_id = $id; verified = $true; crop = $null }
                    if ($act.operation -eq 'set_crop') {
                        $inactive = $s.Mode -eq 'crop-inactive'
                        $row.element_id = $act.view_id
                        $row.crop = [pscustomobject]@{ verified = (-not $inactive); failures = $(if ($inactive) { @('crop_inactive') } else { @() }); crop_box_active = (-not $inactive); viewports = @() }
                    }
                    $rows += $row
                }
                return & $ok ([pscustomobject]@{ rows = $rows })
            }
        }
        return @{ stage = 'apply'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'testrun'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($h) { @(& $module.Run $h.Ctx) }
function OutcomeOf($cases, $i) { ($cases | Where-Object { $_.Name -eq $module.Catalog[$i].Name } | Select-Object -First 1).Outcome }

Write-Host 'docs-dry-run probe module'
$h = New-Fake 'good'; $c = Outcomes $h
Check ($c.Count -eq 4) 'every catalogued case is reported'
foreach ($i in 0..3) { Check ((OutcomeOf $c $i) -eq 'pass') ("fixed tools: case $i passes") }
Check ($h.State.Deleted.Count -ge 5) 'cleanup deletes the level, the walls, the schedule, the plan and the sheet'

Check ((OutcomeOf (Outcomes (New-Fake 'blank')) 0) -eq 'fail') 'a blank top image fails case 0'
Check ((OutcomeOf (Outcomes (New-Fake 'groups-by-length')) 1) -eq 'fail') 'a schedule grouped by Length fails case 1'
Check ((OutcomeOf (Outcomes (New-Fake 'crop-inactive')) 2) -eq 'fail') 'a crop that is not active fails case 2'

$gated = $h.Ctx.PSObject.Copy(); $gated.WriteGate = $true
Check (@(& $module.Run $gated | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 4) 'a closed write tier reports every case not_covered'

if ($fail -gt 0) { Write-Host "$fail failure(s)"; exit 1 }
Write-Host 'all passed'
