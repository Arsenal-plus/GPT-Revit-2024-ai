# Live probe module: horizun_resolve_clash against LOADED LINKS and CONNECTED
# MEP networks (run_shift) - see README.md.
#
# (a) Links a scratch COPY of the write document into itself (same trick as
#     spatial-links.probes.ps1), picks any physical element the link exposes with a
#     readable bounding box, and stages a small/big host pipe crossing CENTERED on
#     that element's plan centre, at an elevation just below its bottom. The
#     elevation-UP escape is exact arithmetic (ClashResolveRulesTests documents the
#     same formula): bigRadius + clearance + smallRadius, independent of the chosen
#     elevation - so moving the small pipe UP by that amount lands it inside the
#     linked element's box. propose must flag that candidate with link_contacts
#     naming the link, and never PROPOSE it.
# (b) Two OWN pipes joined by an elbow (fitting) cross a structural column: the
#     mover is connected, so resolve_clash must collect the network (pipe-elbow-pipe),
#     propose mode=run_shift over all three, and apply must move all three together
#     and re-read both internal connector pairs as still connected.
# Everything created - pipes, elbow, column, level, the link - is deleted afterwards.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'resolve-clash-links-runs'
    Catalog = @(
        @{ Name = 'resolve-clash-links-runs: (a) propose flags the elevation candidate that would touch a linked element, naming it'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'resolve-clash-links-runs: (a) propose never proposes the link-blocked candidate - it is proposed_only if safe, or report_only'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'resolve-clash-links-runs: (a) the link, the pipes and their finding are cleaned up afterwards'; Tool = 'horizun_delete_verified' }
        @{ Name = 'resolve-clash-links-runs: (b) a connected pipe-elbow-pipe run crossing a column proposes mode run_shift over all three'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'resolve-clash-links-runs: (b) apply moves the whole network rigidly and both internal connections re-read connected'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'resolve-clash-links-runs: (b) the pipes, elbow and column are cleaned up afterwards'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'resolve-clash-links-runs' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { foreach ($i in 0..5) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Find-Type($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; max_rows = 500; include_links = $false }
            if (-not $q.data) { return $null }
            $t = @($q.data.rows | Where-Object { $_.is_element_type })
            if ($t.Count -gt 0) { return $t[0].element_id } else { return $null }
        }
        # THE COLUMN TYPE IS CHOSEN BY NAME, NEVER "THE FIRST ONE". MEASURED 2026-09-26: in the
        # full matrix the write tier has already loaded HZC300, a column family authored from the
        # bare structural-column template, and placed without a top level it has NO height
        # (bounding box z 0..0) - the clash and the router saw nothing to avoid. The Autodesk
        # concrete column placed the same way stands 2500 mm.
        function Find-ColumnType {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_StructuralColumns'); include_types = $true; max_rows = 500; include_links = $false }
            if (-not $q.data) { return $null }
            $t = @($q.data.rows | Where-Object { $_.is_element_type -and $_.family -eq 'M_Concrete-Rectangular-Column' -and $_.type -eq '300 x 450mm' })
            if ($t.Count -gt 0) { return $t[0].element_id } else { return $null }
        }

        # ---- scenario (a): a linked element blocks the naive elevation candidate -------
        $created_a = New-Object System.Collections.ArrayList
        $linkTypeId = $null
        try {
            $h = & $Ctx.Call 'horizun_health' @{}
            $me = @($h.data.open_documents | Where-Object { $_.title -eq $doc }) | Select-Object -First 1
            if (-not $me -or -not $me.path -or -not (Test-Path -LiteralPath ([string]$me.path))) {
                foreach ($i in 0..1) { Case $i 'not_covered' ("the write document's path is not readable from health: " + $me.path) }
                throw 'HZ_STOP_A'
            }
            New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
            $src = Join-Path $Ctx.ScratchRoot ('HZ_RCLINKSRC_' + ($run -replace '[^A-Za-z0-9]', '') + '.rvt')
            Copy-Item -LiteralPath ([string]$me.path) -Destination $src -Force
            $add = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; target_document = $doc; path = $src.Replace([char]92, '/') } ($run + '-rclr-add')
            if ($add.stage -ne 'apply' -or $add.answer.isError) { foreach ($i in 0..1) { Case $i 'unverified' ('the link could not be added: ' + (Short $add.answer)) }; throw 'HZ_STOP_A' }
            $linkTypeId = [long]$add.answer.data.link_type_id

            # Any PHYSICAL linked element with height will do; an MEP write model (MEASURED 2026-09-26:
            # the 2023-2027 HVAC fixtures) links no wall, floor or column, but does link equipment.
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls', 'OST_Floors', 'OST_StructuralColumns', 'OST_Columns', 'OST_MechanicalEquipment'); include_links = $true; include_bounding_box = $true; max_rows = 500 }
            $linkEl = @($q.data.rows | Where-Object { $_.source_kind -eq 'link' -and -not $_.is_element_type -and $_.bounding_box -and
                    ([double]$_.bounding_box.max[2] - [double]$_.bounding_box.min[2]) -gt 200 }) | Select-Object -First 1
            if (-not $linkEl) { foreach ($i in 0..1) { Case $i 'not_covered' 'the linked copy exposes no physical element with a usable bounding box' }; throw 'HZ_STOP_A' }
            $bb = $linkEl.bounding_box
            $cx = ([double]$bb.min[0] + [double]$bb.max[0]) / 2; $cy = ([double]$bb.min[1] + [double]$bb.max[1]) / 2; $lz0 = [double]$bb.min[2]

            $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
            $level = if ($lv.data -and @($lv.data.rows).Count -gt 0) { @($lv.data.rows)[0].element_id } else { $null }
            $pipeType = Find-Type 'OST_PipeCurves'; $system = Find-Type 'OST_PipingSystem'
            if (-not $level -or -not $pipeType -or -not $system) { foreach ($i in 0..1) { Case $i 'not_covered' "'$doc' has no level, pipe type or piping system to stage the crossing" }; throw 'HZ_STOP_A' }

            # Exact arithmetic (mirrors AddAxis in Core/ClashResolveRules.cs): with both pipes
            # centred on the SAME Z, the elevation-up escape is bigRadius+clearance+smallRadius
            # regardless of that Z - so this Z is chosen purely to land the MOVED box on the
            # linked element, never to hit the derived distance itself.
            $smallDiam = 50.0; $bigDiam = 150.0; $clearance = 50.0
            $escapeUp = ($bigDiam / 2) + $clearance + ($smallDiam / 2)
            $zTarget = $lz0 - $escapeUp + ($smallDiam / 2)   # moved box bottom lands exactly at lz0

            function New-Pipe($s, $e, $d, $key) {
                $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                    elements = @(@{ kind = 'pipe'; start = $s; end = $e; diameter = $d; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($run + '-rclr-a-' + $key)
                if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return [long]@($r.answer.data.rows)[0].element_id }
                return $null
            }
            $small = New-Pipe @(($cx - 1500), $cy, $zTarget) @(($cx + 1500), $cy, $zTarget) $smallDiam 'small'
            if ($small) { [void]$created_a.Add($small) }
            $big = New-Pipe @($cx, ($cy - 1500), $zTarget) @($cx, ($cy + 1500), $zTarget) $bigDiam 'big'
            if ($big) { [void]$created_a.Add($big) }
            if (-not $small -or -not $big) { foreach ($i in 0..1) { Case $i 'unverified' 'the crossing pipes could not be staged' }; throw 'HZ_STOP_A' }

            $clashArgs = @{ categories_a = @('OST_PipeCurves'); categories_b = @('OST_PipeCurves'); include_links = $false; record_findings = $true; max_results = 500 }
            $null = & $Ctx.Call 'horizun_clash' $clashArgs
            $openA = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; status = 'open'; max_rows = 500 }
            $idsA = @($openA.data.rows | ForEach-Object { [string]$_.finding_id })
            $rowA = $null
            for ($i = 0; $i -lt $idsA.Count -and -not $rowA; $i += 50) {
                $chunk = @($idsA[$i..([Math]::Min($i + 49, $idsA.Count - 1))])
                $p = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'propose'; target_document = $doc; clearance_mm = $clearance; finding_ids = $chunk }
                $rowA = @($p.data.proposals | Where-Object { [long]$_.mover_id -eq $small -and [long]$_.fixed_id -eq $big }) | Select-Object -First 1
            }
            if (-not $rowA) { Case 0 'fail' 'no proposal row matched the small/big pipe pair'; Case 1 'fail' 'no proposal row to check' }
            else {
                $linkHits = @($rowA.candidates | Where-Object { $_.link_contacts })
                $named = @($linkHits | Where-Object { @($_.link_contacts) -like ('*:' + $linkEl.element_id) })
                if ($named.Count -ge 1) { Case 0 'pass' ('candidate ' + $named[0].kind + ' ' + $named[0].distance_mm + ' mm rejected: ' + (@($named[0].link_contacts) -join ',')) }
                else { Case 0 'fail' ('no candidate named link element ' + $linkEl.element_id + ' - candidates: ' + ($rowA.candidates | ConvertTo-Json -Depth 6 -Compress)) }
                $blockedVec = if ($named.Count -ge 1) { (@($named[0].vector_mm) -join ',') } else { $null }
                $proposedVec = if ($rowA.status -eq 'proposed') { (@($rowA.vector_mm) -join ',') } else { $null }
                $safe = ($rowA.status -ne 'proposed') -or ($proposedVec -ne $blockedVec)
                Case 1 $(if ($safe) { 'pass' } else { 'fail' }) ('status=' + $rowA.status + ' vector=' + $proposedVec + ' blocked_vector=' + $blockedVec)
            }
        }
        catch { if ([string]$_ -ne 'HZ_STOP_A') { foreach ($i in 0..1) { if (-not @($out | Where-Object { $_.Name -eq $names[$i].Name }).Count) { Case $i 'unverified' ('probe error: ' + $_) } } } }
        finally {
            $idsA = @($created_a.ToArray()); [array]::Reverse($idsA)
            if ($linkTypeId) { $idsA += $linkTypeId }
            if ($idsA.Count -eq 0) { Case 2 'not_covered' 'nothing was created' }
            else {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $idsA; id_cap = 50 } ($run + '-rclr-a-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 2 'pass' ('deleted ' + ($idsA -join ',')) }
                else { Case 2 'fail' ('left in the disposable document: ' + ($idsA -join ',') + ' - ' + (Short $del.answer)) }
            }
            # THE SOURCE FILE STAYS. MEASURED 2026-09-26 in all four years: deleting the link
            # type does not unload the linked document - Revit keeps it in memory while the
            # delete can still be undone - and the harness-documents manifest declares only
            # files that EXIST in the scratch folder. With the file removed, the matrix driver
            # met a loaded 'HZ_RCLINKSRC_*' nobody declared and left the Revit running as
            # foreign. Kept, it is declared, adopted as the harness's own link, and goes when
            # the write document closes. The scratch folder is the harness's disposable one.
        }

        # ---- scenario (b): a connected pipe-elbow-pipe network crosses a column --------
        $created_b = New-Object System.Collections.ArrayList
        try {
            # AN OWN LEVEL, SO ITS ELEVATION IS KNOWN. MEASURED 2026-09-26: query_model gives
            # a level no bounding box (a datum has no extent), so reading an existing level's
            # Z from one found nothing and scenario (b) never ran in any year.
            $levelZ = 96000.0
            $rl = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'level'; name = ('HZ_RCLR_' + ($run -replace '[^A-Za-z0-9]', '')); elevation = $levelZ }) } ($run + '-rclr-b-level')
            $level = if ($rl.stage -eq 'apply' -and -not $rl.answer.isError) { [long]@($rl.answer.data.rows)[0].element_id } else { $null }
            if ($level) { [void]$created_b.Add($level) }
            $pipeType = Find-Type 'OST_PipeCurves'; $system = Find-Type 'OST_PipingSystem'; $columnType = Find-ColumnType
            # STAGED, NEVER ASSUMED: an MEP fixture carries no structural column family, and
            # Autodesk's structural template of the run's year does (MEASURED 2026-09-26:
            # 'M_Concrete-Rectangular-Column: 300 x 450mm'). The typed copy brings the type in.
            $columnNote = $null
            if (-not $columnType) {
                $tpl = "C:\ProgramData\Autodesk\RVT $($Ctx.Year)\Templates\English\Structural Analysis-DefaultMetric.rte"
                if (-not (Test-Path -LiteralPath $tpl)) { $columnNote = "no structural template at $tpl" }
                else {
                    $cp = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); type_names = @('M_Concrete-Rectangular-Column: 300 x 450mm'); category = 'OST_StructuralColumns'; duplicate_types = 'use_destination' } ($run + '-rclr-b-coltype')
                    $columnType = Find-ColumnType
                    if (-not $columnType) { $columnNote = 'the column type could not be copied from the template: ' + (Short $cp.answer) }
                }
            }
            if (-not $level -or -not $pipeType -or -not $system -or -not $columnType) {
                foreach ($i in 3..4) { Case $i 'not_covered' ("'$doc' gave no own level ($level), pipe type ($pipeType), piping system ($system) or structural column type ($columnType) to stage the network " + $columnNote) }
                throw 'HZ_STOP_B'
            }
            $bx = 900000.0; $by = 0.0; $bz = $levelZ + 300.0
            $r1 = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'pipe'; start = @($bx, $by, $bz); end = @(($bx + 2000), $by, $bz); diameter = 100; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($run + '-rclr-b-p1')
            $pipe1 = if ($r1.stage -eq 'apply' -and -not $r1.answer.isError) { [long]@($r1.answer.data.rows)[0].element_id } else { $null }
            if ($pipe1) { [void]$created_b.Add($pipe1) }
            $r2 = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'pipe'; start = @(($bx + 2000), $by, $bz); end = @(($bx + 2000), ($by + 2000), $bz); diameter = 100; level_id = [long]$level; type_id = [long]$pipeType; system_type_id = [long]$system }) } ($run + '-rclr-b-p2')
            $pipe2 = if ($r2.stage -eq 'apply' -and -not $r2.answer.isError) { [long]@($r2.answer.data.rows)[0].element_id } else { $null }
            if ($pipe2) { [void]$created_b.Add($pipe2) }
            if (-not $pipe1 -or -not $pipe2) { foreach ($i in 3..4) { Case $i 'unverified' ('the two pipes could not be staged: ' + (Short $r1.answer) + ' | ' + (Short $r2.answer)) }; throw 'HZ_STOP_B' }

            $rf = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'fitting'; fitting = 'elbow'; elements = @(@{ element_id = $pipe1 }, @{ element_id = $pipe2 }) }) } ($run + '-rclr-b-elbow')
            $elbow = if ($rf.stage -eq 'apply' -and -not $rf.answer.isError) { [long]@($rf.answer.data.rows)[0].element_id } else { $null }
            if ($elbow) { [void]$created_b.Add($elbow) }
            if (-not $elbow) { foreach ($i in 3..4) { Case $i 'unverified' ('the elbow fitting could not be created: ' + (Short $rf.answer)) }; throw 'HZ_STOP_B' }

            $rc = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'structural_column'; point = @(($bx + 2000), ($by + 1000), 0); coordinate_mode = 'level_offset'; level_id = [long]$level; type_id = [long]$columnType }) } ($run + '-rclr-b-col')
            $column = if ($rc.stage -eq 'apply' -and -not $rc.answer.isError) { [long]@($rc.answer.data.rows)[0].element_id } else { $null }
            if ($column) { [void]$created_b.Add($column) }
            if (-not $column) { foreach ($i in 3..4) { Case $i 'unverified' ('the column could not be created: ' + (Short $rc.answer)) }; throw 'HZ_STOP_B' }

            $clashArgsB = @{ categories_a = @('OST_PipeCurves'); categories_b = @('OST_StructuralColumns'); include_links = $false; record_findings = $true; max_results = 500 }
            $null = & $Ctx.Call 'horizun_clash' $clashArgsB
            $openB = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; status = 'open'; max_rows = 500 }
            $idsB = @($openB.data.rows | ForEach-Object { [string]$_.finding_id })
            $rowB = $null; $proposalB = $null
            for ($i = 0; $i -lt $idsB.Count -and -not $rowB; $i += 50) {
                $chunk = @($idsB[$i..([Math]::Min($i + 49, $idsB.Count - 1))])
                $p = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'propose'; target_document = $doc; finding_ids = $chunk }
                $rowB = @($p.data.proposals | Where-Object { [long]$_.mover_id -eq $pipe2 -and [long]$_.fixed_id -eq $column }) | Select-Object -First 1
                if ($rowB) { $proposalB = @($p.data.next_arguments.proposals | Where-Object { $_.finding_id -eq $rowB.finding_id }) | Select-Object -First 1 }
            }
            if (-not $rowB -or $rowB.status -ne 'proposed') {
                Case 3 'fail' ('pipe2 vs column did not propose: ' + (($rowB | ConvertTo-Json -Depth 6 -Compress) -as [string]))
                Case 4 'fail' 'no accepted proposal to apply'
            }
            else {
                $members = @($rowB.affected_elements | ForEach-Object { [long]$_ })
                $networkOk = $rowB.mode -eq 'run_shift' -and $members.Count -eq 3 -and
                    ($members -contains $pipe1) -and ($members -contains $pipe2) -and ($members -contains $elbow)
                Case 3 $(if ($networkOk) { 'pass' } else { 'fail' }) ('mode=' + $rowB.mode + ' affected_elements=' + ($members -join ','))
                $fidB = [string]$rowB.finding_id
                $apB = & $Ctx.Apply 'horizun_resolve_clash' @{ operation = 'apply'; target_document = $doc
                    proposals = @(@{ finding_id = $fidB; element_id = $pipe2; vector_mm = @($proposalB.vector_mm) }) } ($run + '-rclr-b-apply')
                $props = @($apB.answer.data.postconditions.properties)
                $posOk = @($props | Where-Object { [string]$_.property -like 'position:*' }).Count -eq 3 -and
                    @($props | Where-Object { [string]$_.property -like 'position:*' -and $_.matches -ne $true }).Count -eq 0
                $connProps = @($props | Where-Object { [string]$_.property -like 'connections:*' })
                $connOk = $connProps.Count -eq 2 -and (@($connProps | Where-Object { $_.matches -ne $true }).Count -eq 0)
                $ok = $apB.stage -eq 'apply' -and -not $apB.answer.isError -and $apB.answer.data.postconditions.all_verified -eq $true -and
                    (@($apB.answer.data.findings_resolved_by_model) -contains $fidB) -and $posOk -and $connOk
                Case 4 $(if ($ok) { 'pass' } else { 'fail' }) ('verified=' + $apB.answer.data.postconditions.all_verified + ' positions_ok=' + $posOk +
                    ' connections=' + $connProps.Count + ' connections_ok=' + $connOk + ' text=' + ($apB.answer.text -as [string]))
            }
        }
        catch { if ([string]$_ -ne 'HZ_STOP_B') { foreach ($i in 3..4) { if (-not @($out | Where-Object { $_.Name -eq $names[$i].Name }).Count) { Case $i 'unverified' ('probe error: ' + $_) } } } }
        finally {
            $idsB = @($created_b.ToArray()); [array]::Reverse($idsB)
            if ($idsB.Count -eq 0) { Case 5 'not_covered' 'nothing was created' }
            else {
                $delB = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $idsB; id_cap = 10 } ($run + '-rclr-b-cleanup')
                if ($delB.stage -eq 'apply' -and -not $delB.answer.isError) { Case 5 'pass' ('deleted ' + ($idsB -join ',')) }
                else { Case 5 'fail' ('left in the disposable document: ' + ($idsB -join ',') + ' - ' + (Short $delB.answer)) }
            }
        }
        return $out.ToArray()
    }
}
