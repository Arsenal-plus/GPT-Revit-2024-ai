#Requires -Version 5.1
# Exercises links-survey.probes.ps1 WITHOUT Revit. Reply shapes come from the code
# (ManageLinksCoordinates.cs / ManageLinksPointCloud.cs / ManageLinksIfc.cs, and
# VerifiedModelEdit publishing edit.Result under `result`; add kind=ifc answers its
# payload at the top level) - shapes from the code, to be held against the first live
# run. The fixtures file is redirected to a temp USERPROFILE and the Revit program
# folder to a temp RevitRoot, so the importer-absent rule is exercised both ways.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'links-survey.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'links-survey' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }
$realProfile = $env:USERPROFILE

function New-Fake([string]$mode, [bool]$withFixtures, [bool]$importerOnDisk = $true) {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-ls-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path (Join-Path $root '.horizun') | Out-Null
    $revitRoot = Join-Path $root 'Autodesk'
    New-Item -ItemType Directory -Force -Path (Join-Path $revitRoot 'Revit 2026') | Out-Null
    if ($importerOnDisk) { Set-Content -LiteralPath (Join-Path $revitRoot 'Revit 2026\Revit.IFC.Import.dll') -Value 'dll' -Encoding ascii }
    $src = Join-Path $root 'host.rvt'; Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $pc = Join-Path $root 'scan.rcp'; Set-Content -LiteralPath $pc -Value 'rcp' -Encoding ascii
    $ifc = Join-Path $root 'model.ifc'; Set-Content -LiteralPath $ifc -Value 'ifc' -Encoding ascii
    $fx = if ($withFixtures) { @{ PointCloudPath = $pc; IfcLinkSource = $ifc; PointCloudFloor = @{ min_xy = @(0, 0); max_xy = @(4000, 4000); z = 0 } } } else { @{} }
    ($fx | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath (Join-Path $root '.horizun\live-fixtures.json') -Encoding ascii
    $env:USERPROFILE = $root
    $s = @{ Mode = $mode; Next = 500; Deleted = @(); Restored = $null; Acquired = $false; Instances = 1; Src = $src; Kinds = @{}; Exported = $null; InstancesAtApply = $null; TwiceAsked = $null; TypeOf = @{} }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $before = [pscustomobject]@{ east_west = 1.5; north_south = 2.5; elevation = 0; angle_to_true_north = 12 }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $s.Src }) }) }
            'horizun_query_model' {
                $rows = if ([string]@($a.categories)[0] -eq 'OST_Walls') { @([pscustomobject]@{ element_id = 70; is_element_type = $true; family = 'Curtain Wall'; type = 'Curtain Wall 1' }, [pscustomobject]@{ element_id = 72; is_element_type = $true; family = 'Basic Wall'; type = 'Exterior - Brick' }, [pscustomobject]@{ element_id = 71; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' }) } else { @([pscustomobject]@{ element_id = 80; is_element_type = $true; family = 'Floor'; type = 'Generic 150mm' }) }
                if ($s.Mode -eq 'no-types') { $rows = @() }
                return & $reply ([pscustomobject]@{ rows = $rows })
            }
            'horizun_federation_check' {
                $st = if ($s.Acquired -or $s.Mode -eq 'rollback-broken') { 'coherent' } else { 'incoherent' }
                return & $reply ([pscustomobject]@{ site = @([pscustomobject]@{ instance_id = 901; state = $st; max_delta_mm = 10000 }) })
            }
            'horizun_manage_links' {
                if ($a.operation -eq 'acquire_coordinates') {
                    if ($s.Instances -gt 1) { $s.TwiceAsked = $(if ($s.Acquired) { 'after-acquire' } else { 'off-site' }) }
                    # The tool's own refusal once the link shares the host's site (LinkSurveyRules.AcquireRefusal): Revit is never asked.
                    if ($s.Acquired) { return & $reply $null $true 'instance 901 already shares the host''s coordinates (same_site delta 0 mm <= 1 mm); acquiring would change nothing and Revit refuses it. Nothing was written.' }
                    if ($s.Instances -gt 1) {
                        if ($s.Mode -eq 'twice-accepted') { return & $reply ([pscustomobject]@{ dry_run = $true; rehearsal = [pscustomobject]@{ applied_and_verified = $true; rollback_status = 'RolledBack'; error = $null; postconditions = [pscustomobject]@{ all_verified = $true } } }) }
                        if ($s.Mode -eq 'stale-addin') { return & $reply $null $true 'the link type of instance 901 is placed 2 times (901, 902). Revit refuses to acquire coordinates from a model placed multiple times' }
                        return & $reply $null $true 'The rehearsal failed: Cannot acquire coordinates from a model placed multiple times.'
                    }
                    return & $reply ([pscustomobject]@{ dry_run = $true; plan = [pscustomobject]@{ project_position_before = $before }; rehearsal = [pscustomobject]@{ rolled_back = $true } })
                }
                if ($a.operation -eq 'scan_deviation') {
                    $id = [long]@($a.element_ids)[0]
                    if ($s.Kinds[$id] -eq 'floor') {
                        $topFace = [pscustomobject]@{ face = 0; normal = @(0, 0, 1); points = 800; state = 'ok'; p95_abs_mm = 4.2; point_frame = 'identity'; coverage_share = 0.93; average_distance_mm = 50; coverage_grid = '20x20' }
                        $bottom = [pscustomobject]@{ face = 1; normal = @(0, 0, -1); points = 0; state = 'not_measured'; reason = 'too_few_points' }
                        return & $reply ([pscustomobject]@{ verdict = 'not_decidable'; min_points_per_face = 20; elements = @([pscustomobject]@{ element_id = $id; state = 'partially_measured'; faces = @($topFace, $bottom) }) })
                    }
                    $why = if ($s.Mode -eq 'unreadable') { 'points_unreadable' } else { 'too_few_points' }
                    $faces = @(1..6 | ForEach-Object { [pscustomobject]@{ face = $_; normal = @(0, 1, 0); state = 'not_measured'; reason = $why; points = 0 } })
                    return & $reply ([pscustomobject]@{ verdict = 'not_decidable'; min_points_per_face = 20; elements = @([pscustomobject]@{ element_id = $id; state = 'not_measured'; faces = $faces }) })
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
                if ($a.operation -eq 'acquire_coordinates') {
                    $s.InstancesAtApply = $s.Instances
                    # Revit refuses to acquire from a model placed twice: the rehearsal fails and no token is issued.
                    if ($s.Instances -gt 1) { return @{ stage = 'dry_run'; answer = [pscustomobject]@{ isError = $true; data = $null; text = 'The rehearsal failed: Cannot acquire coordinates from a model placed multiple times.' } } }
                    $s.Acquired = $true
                    if ($s.Mode -eq 'apply-error') { return @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $true; data = $null; text = 'The group assimilated but the re-read did not hold; state is uncertain.' } } }
                    return & $ok ([pscustomobject]@{ result = [pscustomobject]@{ same_site = $true; same_site_delta_mm_after = 0.0 } })
                }
                if ($a.operation -eq 'add_instance') { $s.Instances = 2; return & $ok ([pscustomobject]@{ link_instance_id = 902 }) }
                if ($a.kind -eq 'point_cloud') { return & $ok ([pscustomobject]@{ result = [pscustomobject]@{ link_type_id = 950; link_instance_id = 951; engine = 'rcp'; verified = $true } }) }
                if ($a.kind -eq 'ifc') {
                    if ($s.Mode -eq 'no-importer') { return @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $true; data = [pscustomobject]@{ reason = 'ifc_importer_unavailable' }; text = 'ifc_importer_unavailable: Revit 2026 has no IFC importer (looked in: ...)' } } }
                    if ($s.Mode -eq 'import-failed') { return @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $true; data = [pscustomobject]@{ reason = 'ifc_import_failed' }; text = 'ifc_import_failed: Revit 2026''s IFC importer refused ...' } } }
                    $shapes = if ($s.Mode -eq 'empty-ifc') { 0 } else { 12 }
                    return & $ok ([pscustomobject]@{ link_type_id = 960; link_instance_id = 961; verified = ($shapes -gt 0); linked_direct_shapes = $shapes; intermediate_rvt = 'x.ifc.RVT'; linked_by = 'RevitLinkType.CreateFromIFC' })
                }
                return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 })
            }
            'horizun_export' { $s.Exported = $a.output_path; Set-Content -LiteralPath $a.output_path -Value 'ISO-10303-21;' -Encoding ascii; return & $ok ([pscustomobject]@{ output_path = $a.output_path }) }
            'horizun_transform_elements' { return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_manage_units' { $s.Restored = $a.project_position; return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_delete_verified' {
                if ($s.Mode -eq 'rm2-fails' -and @($a.ids) -contains 902) { return @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $true; data = $null; text = 'refused: 902 could not be deleted' } } }
                if (@($a.ids) -contains 902) { $s.Instances = 1 }
                $s.Deleted = @($s.Deleted) + @($a.ids); return & $ok ([pscustomobject]@{ ok = $true })
            }
            'horizun_create_elements' { $id = $s.Next; $s.Next++; $s.Kinds[[long]$id] = [string]@($a.elements)[0].kind; $s.TypeOf[[long]$id] = @($a.elements)[0].type_id; return & $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) }) }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $root 'scratch'); RunId = 't1'; WriteGate = $false; RevitRoot = $revitRoot; LinkSourceDocument = $src; Call = $call; Apply = $apply } }
}

try {
    $h = New-Fake 'ok' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r.Count -eq 8) 'eight cases'
    Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('all pass with fixtures: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
    Check ($h.State.Restored -and [double]$h.State.Restored.east_west -eq 1.5 -and [double]$h.State.Restored.angle_to_true_north -eq 12) 'shared position restored from the dry run''s project_position_before'
    Check ((@(900, 950, 960, 500, 501, 502, 503) | Where-Object { $h.State.Deleted -notcontains $_ }).Count -eq 0) ('link types, point cloud type, levels, wall and floor deleted: ' + ($h.State.Deleted -join ','))
    Check ($r[2].Detail -match 'Revit refused') 'a type placed twice is answered by Revit for the named instance'
    Check ($r[5].Detail -match 'points=800' -and $r[5].Detail -match 'frame=identity') 'the floor''s top face is measured, with its points and frame'
    Check ($h.State.TwiceAsked -eq 'off-site') 'the placed-twice question is asked while the site still differs, so Revit answers it'
    Check ($h.State.InstancesAtApply -eq 1) 'the apply runs after the second placement is removed'
    Check ([long]$h.State.TypeOf[[long]501] -eq 71 -and [long]$h.State.TypeOf[[long]503] -eq 80) 'wall and floor staged with a Generic basic wall and a floor type, not the default or a curtain wall'

    $h = New-Fake 'rm2-fails' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[1].Outcome -eq 'unverified' -and $r[1].Detail -match 'could not be removed' -and $r[2].Outcome -eq 'pass' -and $null -eq $h.State.InstancesAtApply) 'a second placement that cannot be removed stops before the apply'
    $h = New-Fake 'no-types' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[4].Outcome -eq 'not_covered' -and $r[4].Detail -match 'basic wall type' -and $r[5].Outcome -eq 'not_covered' -and $r[5].Detail -match 'floor type') 'without a basic wall or a floor type the scan cases are not_covered, named'

    $h = New-Fake 'ok' $false; $r = @(& $module.Run $h.Ctx)
    Check (($r[3].Outcome -eq 'not_covered') -and ($r[3].Detail -match 'PointCloudPath') -and ($r[5].Detail -match 'PointCloudPath')) 'missing point cloud fixture is not_covered naming its key'
    Check ($r[6].Outcome -eq 'pass' -and $r[6].Detail -match 'exported from HZ_WRITE' -and $h.State.Exported) 'without IfcLinkSource the IFC case exports its own IFC and is measured'
    Check ($r[0].Outcome -eq 'pass' -and $r[7].Outcome -eq 'pass') 'acquire still runs and cleans up without the file fixtures'

    $h = New-Fake 'rollback-broken' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[0].Outcome -eq 'fail') 'a dry run that leaves the link coherent fails the rollback case'

    $h = New-Fake 'no-importer' $true $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[6].Outcome -eq 'pass' -and $r[6].Detail -match 'has no importer') 'ifc_importer_unavailable passes only with the importer really absent'
    $h = New-Fake 'no-importer' $true $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[6].Outcome -eq 'fail' -and $r[6].Detail -match 'exists') 'ifc_importer_unavailable with the importer on disk FAILS'
    $h = New-Fake 'import-failed' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[6].Outcome -eq 'fail') 'any other IFC refusal fails the case'
    $h = New-Fake 'empty-ifc' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[6].Outcome -eq 'fail') 'an IFC link without content is not a pass'

    $h = New-Fake 'apply-error' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[1].Outcome -eq 'fail' -and $h.State.Restored -and $r[7].Outcome -eq 'pass' -and $r[7].Detail -match 'restored=True') 'an apply that answered an error is still restored'

    $h = New-Fake 'unreadable' $true; $r = @(& $module.Run $h.Ctx)
    Check ($r[4].Outcome -eq 'fail') 'a far wall whose faces are points_unreadable does not pass as unreached'

    $h = New-Fake 'stale-addin' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[2].Outcome -eq 'fail') 'the tool''s own pre-refusal is not Revit''s answer'
    $h = New-Fake 'twice-accepted' $false; $r = @(& $module.Run $h.Ctx)
    Check ($r[2].Outcome -eq 'pass' -and $r[2].Detail -match 'accepted') 'Revit accepting the named instance is recorded as its answer'

    $h = New-Fake 'ok' $true; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
    Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 8) 'write tier closed: all not_covered'
}
finally { $env:USERPROFILE = $realProfile }
if ($fail -gt 0) { Write-Host "$fail check(s) failed"; exit 1 } else { Write-Host 'links-survey probe tests: all passed' }
