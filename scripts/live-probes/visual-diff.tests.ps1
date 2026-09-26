#Requires -Version 5.1
# Exercises visual-diff.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a model where
# a snapshot is taken, a wall appears inside the frame, and the diff sees it.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'visual-diff.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'visual-diff' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Views = 40; Deleted = @(); Wall = $null; Files = @(); Dir = (Join-Path $env:TEMP ('hz-vd-' + [guid]::NewGuid().ToString('N'))) }
    New-Item -ItemType Directory -Path $s.Dir | Out-Null
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' {
                if ($s.Mode -eq 'no-types') { return & $reply ([pscustomobject]@{ rows = @() }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) })
            }
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ total = $s.Views }) }
            'horizun_verify_changes' {
                if ($s.Mode -eq 'leaks-view') { $s.Views++ }
                if ($a.operation -eq 'snapshot') {
                    $png = Join-Path $s.Dir ($a.snapshot_name + '.png'); $json = Join-Path $s.Dir ($a.snapshot_name + '.json')
                    Set-Content -LiteralPath $png -Value 'png' -Encoding ascii; Set-Content -LiteralPath $json -Value '{}' -Encoding ascii
                    $s.Files += @($png, $json)
                    return & $reply ([pscustomobject]@{ status = 'ok'; operation = 'snapshot'; baseline_png = $png; baseline_camera = $json; width = 800; height = 600; temporary_view_rollback = 'RolledBack' })
                }
                $diff = Join-Path $s.Dir ('diff-' + [guid]::NewGuid().ToString('N') + '.png'); Set-Content -LiteralPath $diff -Value 'png' -Encoding ascii
                $fresh = $a.snapshot_name -like '*_after'
                $ratio = if ($fresh) { if ($s.Mode -eq 'noisy') { 0.02 } else { 0.0 } } else { 0.013 }
                $named = if ($s.Mode -eq 'no-region') { @(555) } else { @($s.Wall) }
                $regions = if ($ratio -gt 0) { @([pscustomobject]@{ pixel_bbox = @(300, 200, 420, 260); pixel_count = 5000; element_ids = $named }) } else { @() }
                $changed = if ($fresh) { @() } else { @($s.Wall) }
                return & $reply ([pscustomobject]@{ status = 'ok'; operation = 'compare_to'; changed_pixel_ratio = $ratio; diff_path = $diff; regions = $regions
                    elements_changed_since_baseline = $changed; temporary_view_rollback = 'RolledBack' })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        if ($tool -eq 'horizun_delete_verified') { $s.Deleted = @($a.ids); return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        $id = $s.Next; $s.Next++
        if ($key -like '*new-wall') { $s.Wall = $id }
        return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($h) { @(& $module.Run $h.Ctx) }

$h = New-Fake 'ok'; $r = Outcomes $h
Check ($r.Count -eq 6) 'six cases, one per catalog entry'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('snapshot, diff, attribution, ~0 recompare, no view left and cleanup all pass: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check ($h.State.Deleted.Count -eq 4) 'the level, the two anchors and the new wall are deleted'
Check (@($h.State.Files | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0) 'the baseline PNG and camera files are removed'

$h = New-Fake 'no-region'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'fail') 'a region that does not name the new wall fails the attribution case'
Check ($r[1].Outcome -eq 'pass') '... while the changed-pixel case still passes on its own'

$h = New-Fake 'noisy'; $r = Outcomes $h
Check ($r[3].Outcome -eq 'fail') 'a recompare of an unchanged scene with a visible ratio fails the ~0 case'

$h = New-Fake 'leaks-view'; $r = Outcomes $h
Check ($r[4].Outcome -eq 'fail') 'a view count that moved fails the nothing-left-behind case'

$h = New-Fake 'no-types'; $r = Outcomes $h
Check (@($r[0..4] | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 5) 'a fixture without a basic wall type is not_covered, never a pass'
Check ($h.State.Deleted.Count -eq 1) 'the staged level is still deleted'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = Outcomes $h
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 6) 'with the write tier closed every case is not_covered'

if ($fail -gt 0) { Write-Host "visual-diff probe tests: $fail FAILED"; exit 1 }
Write-Host 'visual-diff probe tests: ALL PASS'
