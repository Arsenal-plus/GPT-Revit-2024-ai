#Requires -Version 5.1
# Exercises links-survey.probes.ps1 WITHOUT Revit. Reply shapes come from the code
# (ManageLinksCoordinates.cs / ManageLinksPointCloud.cs / ManageLinksIfc.cs, and
# VerifiedModelEdit publishing edit.Result under `result`) - to be held against the
# first live run. The fixtures file is redirected to a temp USERPROFILE.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'links-survey.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'links-survey' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }
$realProfile = $env:USERPROFILE

function New-Fake([string]$mode, [bool]$withFixtures) {
    $root = Join-Path $env:TEMP ('hz-ls-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path (Join-Path $root '.horizun') | Out-Null
    $src = Join-Path $root 'host.rvt'; Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $pc = Join-Path $root 'scan.rcp'; Set-Content -LiteralPath $pc -Value 'rcp' -Encoding ascii
    $ifc = Join-Path $root 'model.ifc'; Set-Content -LiteralPath $ifc -Value 'ifc' -Encoding ascii
    $fx = if ($withFixtures) { @{ PointCloudPath = $pc; IfcLinkSource = $ifc } } else { @{} }
    ($fx | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $root '.horizun\live-fixtures.json') -Encoding ascii
    $env:USERPROFILE = $root
    $s = @{ Mode = $mode; Next = 500; Deleted = @(); Restored = $null; Acquired = $false; Instances = 1; Src = $src }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $before = [pscustomobject]@{ east_west = 1.5; north_south = 2.5; elevation = 0; angle_to_true_north = 12 }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $s.Src }) }) }
            'horizun_federation_check' {
                $st = if ($s.Acquired -or $s.Mode -eq 'rollback-broken') { 'coherent' } else { 'incoherent' }
                return & $reply ([pscustomobject]@{ site = @([pscustomobject]@{ instance_id = 901; state = $st; max_delta_mm = 10000 }) })
            }
            'horizun_manage_links' {
                if ($a.operation -eq 'acquire_coordinates') {
                    if ($s.Instances -gt 1) { return & $reply $null $true 'the link type of instance 901 is placed 2 times (901, 902). Revit refuses ...' }
                    return & $reply ([pscustomobject]@{ dry_run = $true; plan = [pscustomobject]@{ project_position_before = $before }; rehearsal = [pscustomobject]@{ rolled_back = $true } })
                }
                if ($a.operation -eq 'scan_deviation') {
                    $faces = @(1..6 | ForEach-Object { [pscustomobject]@{ face = $_; state = 'not_measured'; reason = 'too_few_points'; points = 0 } })
                    return & $reply ([pscustomobject]@{ verdict = 'not_decidable'; elements = @([pscustomobject]@{ element_id = $a.element_ids[0]; state = 'not_measured'; faces = $faces }) })
                }
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_manage_links' {
                if ($a.operation -eq 'acquire_coordinates') { $s.Acquired = $true; return & $ok ([pscustomobject]@{ result = [pscustomobject]@{ same_site = $true; same_site_delta_mm_after = 0.0 } }) }
                if ($a.operation -eq 'add_instance') { $s.Instances = 2; return & $ok ([pscustomobject]@{ link_instance_id = 902 }) }
                if ($a.kind -eq 'point_cloud') { return & $ok ([pscustomobject]@{ result = [pscustomobject]@{ link_type_id = 950; link_instance_id = 951; engine = 'rcp'; verified = $true } }) }
                if ($a.kind -eq 'ifc') {
                    if ($s.Mode -eq 'no-importer') { return @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $true; data = $null; text = 'ifc_importer_unavailable: Revit 2023 could not import ...' } } }
                    return & $ok ([pscustomobject]@{ link_type_id = 960; link_instance_id = 961; verified = $true; intermediate_rvt = 'x.ifc.RVT'; linked_by = 'RevitLinkType.CreateFromIFC' })
                }
                return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 })
            }
            'horizun_transform_elements' { return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_manage_units' { $s.Restored = $a.project_position; return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_delete_verified' { $s.Deleted = @($a.ids); return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_create_elements' { $id = $s.Next; $s.Next++; return & $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }) }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $root 'scratch'); RunId = 't1'; WriteGate = $false; Call = $call; Apply = $apply } }
}

try {
    $h = New-Fake 'ok' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r.Count -eq 7) 'seven cases'
    Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('all pass with fixtures: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
    Check ($h.State.Restored -and [double]$h.State.Restored.east_west -eq 1.5 -and [double]$h.State.Restored.angle_to_true_north -eq 12) 'shared position restored from the dry run''s project_position_before'
    Check ((@(900, 950, 960, 500, 501) | Where-Object { $h.State.Deleted -notcontains $_ }).Count -eq 0) ('link types, point cloud type, level and wall deleted: ' + ($h.State.Deleted -join ','))

    $h = New-Fake 'ok' $false; $r = @(& $module.Run $h.Ctx)
    Check (($r[3].Outcome -eq 'not_covered') -and ($r[3].Detail -match 'PointCloudPath') -and ($r[5].Detail -match 'IfcLinkSource')) 'missing fixtures are not_covered naming their keys'
    Check ($r[0].Outcome -eq 'pass' -and $r[6].Outcome -eq 'pass') 'acquire still runs and cleans up without the file fixtures'

    $h = New-Fake 'rollback-broken' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[0].Outcome -eq 'fail') 'a dry run that leaves the link coherent fails the rollback case'

    $h = New-Fake 'no-importer' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[5].Outcome -eq 'pass' -and $r[5].Detail -match 'refused by name') 'a missing IFC importer refused by name is the year''s honest answer'

    $h = New-Fake 'ok' $true; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
    Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 7) 'write tier closed: all not_covered'
}
finally { $env:USERPROFILE = $realProfile }
if ($fail -gt 0) { Write-Host "$fail check(s) failed"; exit 1 } else { Write-Host 'links-survey probe tests: all passed' }
