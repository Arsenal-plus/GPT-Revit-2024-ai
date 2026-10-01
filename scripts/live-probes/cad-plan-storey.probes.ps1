#Requires -Version 5.1
# Live probe module: the typed DWG -> model path on a level that is NOT at zero.
#
# MEASURED (dry run, class 4 of the course report): a DWG exported by Revit and linked
# into a plan of a new level at +30 000 mm was planned into 75 walls, and then
#   - horizun_apply_cad_plan refused coherence_unknown on a machine where the text
#     extractor had never run, and nothing the caller could do cleared it;
#   - every planned wall carried Z = 0, so applied as emitted it would have been
#     built 30 m below its level;
#   - the plan reply was 232 kB and the client truncated it;
#   - a TwoLevelsBased structural column type was refused with no way to state its top.
#
# This module stages exactly that shape in the disposable document - an own plan with
# two walls exported to DWG, the walls deleted, a second level 30 m above, the DWG
# linked into a plan of THAT level - and checks each of the four. Everything it
# creates is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'cad-plan-storey'
    Catalog = @(
        @{ Name = 'cad-plan-storey: stage two walls, export them to DWG and link it into a plan of a level 30 m up'; Tool = 'horizun_manage_cad_links' }
        @{ Name = 'cad-plan-storey: a walls-only plan of that link is applicable on the link alone (link_geometry_only)'; Tool = 'horizun_plan_from_cad' }
        @{ Name = 'cad-plan-storey: every planned wall stands on the elevated level, not at the drawing Z'; Tool = 'horizun_plan_from_cad' }
        @{ Name = 'cad-plan-storey: response_mode=summary is smaller, keeps apply_binding and names what it cut'; Tool = 'horizun_plan_from_cad' }
        @{ Name = 'cad-plan-storey: apply by plan_id builds and verifies every planned wall'; Tool = 'horizun_apply_cad_plan' }
        @{ Name = 'cad-plan-storey: a TwoLevelsBased column type is refused without top_level and usable with it'; Tool = 'horizun_plan_from_cad' }
        @{ Name = 'cad-plan-storey: cleanup deletes everything the module created'; Tool = 'horizun_delete_verified' }
    )
    # The walls-only requirement set the plan reads the link with. Nothing in it reads the
    # drawing FILE (no blocks, solid_hatch_layers or section), which is the case #2 is about.
    WallSet = {
        param([string]$layer, [string]$units, [string]$levelName)
        @{
            schema = 'horizun.cad-requirements/1'
            requirement_set = @{ id = 'hz-live-cad-plan-storey'; version = '1.0.0'; title = 'Live probe: walls on an elevated level' }
            source = @{ units = $units }
            tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
            rules = @(@{
                id = 'walls'; precedence = 10; discipline = 'architecture'; layers = @($layer); produces = 'wall'
                category = 'OST_Walls'; height_mm = 3000.0; level = $levelName
                geometry = @{ from = 'double_lines'; min_thickness_mm = 80.0; max_thickness_mm = 450.0
                              min_overlap_mm = 1000.0; min_overlap_fraction = 0.6 }
            })
        }
    }
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $self = @($script:HzProbeModules | Where-Object { $_.Name -eq 'cad-plan-storey' })[0]
        $catalog = $self.Catalog
        function Out-Case($i, $outcome, $detail) {
            $cases.Add(@{ Name = $catalog[$i].Name; Tool = $catalog[$i].Tool; Outcome = $outcome; Detail = [string]$detail })
        }
        function Short($answer) {
            if ($null -eq $answer) { return 'no answer' }
            $t = [string]$answer.text
            if ($t.Length -gt 300) { $t = $t.Substring(0, 300) + '...' }
            return $t
        }
        function Applied($r) { return ($r -and $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) }
        function FirstId($r, $alias) {
            if (-not (Applied $r)) { return $null }
            $d = $r.answer.data
            if ($alias -and $d.aliases -and $d.aliases.$alias) { return [long]$d.aliases.$alias }
            $row = @($d.rows)[0]
            if ($row -and $row.element_id) { return [long]$row.element_id }
            if ($d.element_id) { return [long]$d.element_id }
            return $null
        }

        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Out-Case $i 'not_covered' 'write tier closed: this module commits into the disposable document' }
            return $cases.ToArray()
        }
        $doc = $Ctx.Document
        $tag = ([string]$Ctx.RunId) -replace '[^A-Za-z0-9]', ''
        $created = New-Object System.Collections.Generic.List[long]   # deleted at the end, in this order
        $levels = New-Object System.Collections.Generic.List[long]    # deleted LAST: a level takes its views with it
        $ea = 93000.0; $eb = $ea + 30000.0; $x0 = 985000.0

        # ---- stage: own level A with two walls and a plan of it, exported; level B 30 m up ----
        $instance = $null; $levelB = $null; $planB = $null; $levelBName = "HZ_CPS_B_$tag"
        $la = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(
                @{ kind = 'level'; name = "HZ_CPS_A_$tag"; elevation = $ea }) } 'cps-level-a'
        $levelA = FirstId $la $null
        $lb = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(
                @{ kind = 'level'; name = $levelBName; elevation = $eb }) } 'cps-level-b'
        $levelB = FirstId $lb $null
        foreach ($id in @($levelA, $levelB)) { if ($id) { $levels.Add($id) } }
        $walls = @()
        if ($levelA) {
            $w = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(
                    @{ kind = 'wall'; start = @($x0, 0.0, $ea); end = @(($x0 + 6000), 0.0, $ea); height = 3000.0; level_id = $levelA }
                    @{ kind = 'wall'; start = @(($x0 + 6000), 0.0, $ea); end = @(($x0 + 6000), 4000.0, $ea); height = 3000.0; level_id = $levelA }) } 'cps-walls'
            if (Applied $w) { $walls = @(@($w.answer.data.rows) | ForEach-Object { [long]$_.element_id }) }
        }
        $planA = $null
        if ($levelA -and $levelB) {
            $v = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                    @{ operation = 'create_floor_plan'; level_id = $levelA; name = "HZ_CPS_PA_$tag"; key = 'pa' }
                    @{ operation = 'create_floor_plan'; level_id = $levelB; name = "HZ_CPS_PB_$tag"; key = 'pb' }) } 'cps-plans'
            if ((Applied $v) -and $v.answer.data.aliases) { $planA = [long]$v.answer.data.aliases.pa; $planB = [long]$v.answer.data.aliases.pb }
            foreach ($id in @($planA, $planB)) { if ($id) { $created.Add($id) } }
        }
        $dwg = $null
        if ($planA -and $walls.Count -eq 2) {
            $out = Join-Path $Ctx.ScratchRoot "HZ_CPS_$tag.dwg"
            $x = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg'; view_ids = @($planA); output_path = $out } 'cps-export'
            $made = @(Get-ChildItem -LiteralPath $Ctx.ScratchRoot -Filter "HZ_CPS_$tag*.dwg" -ErrorAction SilentlyContinue)
            if ((Applied $x) -and $made.Count -ge 1) { $dwg = $made[0].FullName }
            # THE DRAWING IS A PICTURE: the walls it was drawn from go, or the plan would see them standing.
            $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($walls); id_cap = 10 } 'cps-walls-gone'
        }
        if ($dwg -and $planB) {
            $l = & $Ctx.Apply 'horizun_manage_cad_links' @{ target_document = $doc; operation = 'add'; file_path = $dwg
                    view_id = $planB; units = 'default'; current_view_only = $true } 'cps-link'
            if ((Applied $l) -and $l.answer.data.host_verified -eq $true) { $instance = [long]$l.answer.data.element_id; $created.Insert(0, $instance) }
        }
        if ($instance) { Out-Case 0 'pass' ("DWG {0} linked as instance {1} into plan {2} of level {3} at {4} mm" -f (Split-Path -Leaf $dwg), $instance, $planB, $levelB, $eb) }
        else {
            Out-Case 0 'fail' ("staging stopped: levelA={0} levelB={1} walls={2} planA={3} planB={4} dwg={5}" -f $levelA, $levelB, $walls.Count, $planA, $planB, $dwg)
            for ($i = 1; $i -le 4; $i++) { Out-Case $i 'unverified' 'needs the staged link' }
        }

        # ---- the plan ----------------------------------------------------------------------
        $full = $null; $set = $null
        if ($instance) {
            $inst = & $Ctx.Call 'horizun_query_cad' @{ mode = 'instances' }
            $facts = @(if ($inst.data) { @($inst.data.instances) | Where-Object { [long]$_.element_id -eq $instance } })[0]
            $lay = & $Ctx.Call 'horizun_query_cad' @{ mode = 'layers'; instance_id = $instance }
            $wallLayer = @(if ($lay.data) { @($lay.data.layers) | Where-Object { [string]$_.layer -match '(?i)WALL' } })[0]
            if (-not $facts -or -not $wallLayer) {
                for ($i = 1; $i -le 4; $i++) { Out-Case $i 'unverified' ('the link has no readable instance facts or no WALL layer: ' + (Short $lay)) }
            }
            else {
                # The unit the gate compares: the one this bridge measured Revit applied, when it did (#unit basis).
                $units = if ($facts.applied_units) { [string]$facts.applied_units } else { [string]$facts.declared_units }
                $set = & $self.WallSet ([string]$wallLayer.layer) $units $levelBName
                $full = & $Ctx.Call 'horizun_plan_from_cad' @{ target_document = $doc; instance_id = $instance; requirement_set = $set }
                if ($full.isError -or -not $full.data) {
                    for ($i = 1; $i -le 4; $i++) { Out-Case $i 'fail' ('plan_from_cad: ' + (Short $full)) }
                    $full = $null
                }
            }
        }
        $rows = @()
        if ($full) {
            $p = $full.data
            # #2 - the coherence a walls-only plan is judged on
            $co = $p.coherence
            if ($p.applicable -eq $true -and [string]$co.state -eq 'link_geometry_only' -and [string]$co.basis -eq 'link_geometry_and_host_file') {
                Out-Case 1 'pass' ("applicable; state {0}, basis {1}, references_checked {2}" -f $co.state, $co.basis, $co.references_checked)
            }
            elseif ($p.applicable -eq $true -and [string]$co.state -eq 'sources_match_the_link') {
                Out-Case 1 'pass' 'applicable on the stronger basis: this machine had read the drawing through the text extractor already'
            }
            else { Out-Case 1 'fail' ("applicable={0} state={1} why={2} remedy={3}" -f $p.applicable, $co.state, $co.why, $co.remedy) }

            # #3 - Z of every planned wall is the storey, not the drawing
            foreach ($a in @($p.execute_plan_request.actions)) { foreach ($r in @($a.arguments.elements)) { if ($r.kind -eq 'wall') { $rows += $r } } }
            $storey = $p.storey_placement.levels.$levelBName
            $zs = @($rows | ForEach-Object { [double]$_.start[2]; [double]$_.end[2] })
            $at = if ($storey) { [double]$storey.level_elevation_mm } else { [double]::NaN }
            $onLevel = $rows.Count -ge 2 -and -not [double]::IsNaN($at) -and
                       @($zs | Where-Object { [Math]::Abs($_ - $at) -gt 1.0 }).Count -eq 0 -and
                       [Math]::Abs($at - $ea) -gt 29000.0
            if ($onLevel) { Out-Case 2 'pass' ("{0} wall(s) at Z {1} mm = level {2}; drawn Z was {3} on {4} row(s)" -f $rows.Count, $at, $levelBName, $storey.drawn_z_mm, $storey.rows_whose_drawn_z_was_not_the_storey) }
            else { Out-Case 2 'fail' ("walls={0} Z=[{1}] storey elevation={2} (level A at {3})" -f $rows.Count, ($zs -join ','), $at, $ea) }

            # #17 - the summary
            $sum = & $Ctx.Call 'horizun_plan_from_cad' @{ target_document = $doc; instance_id = $instance; requirement_set = $set; response_mode = 'summary' }
            if ($sum.data) {
                $fullBytes = [Text.Encoding]::UTF8.GetByteCount(($p | ConvertTo-Json -Depth 40 -Compress))
                $sumBytes = [Text.Encoding]::UTF8.GetByteCount(($sum.data | ConvertTo-Json -Depth 40 -Compress))
                $sameBinding = (($sum.data.apply_binding | ConvertTo-Json -Depth 20 -Compress) -eq ($p.apply_binding | ConvertTo-Json -Depth 20 -Compress))
                if ($sum.data.response_mode -eq 'summary' -and $sumBytes -le $fullBytes -and $sameBinding -and $sum.data.plan_id -eq $p.plan_id) {
                    Out-Case 3 'pass' ("summary {0} B against full {1} B; {2} list(s) cut; binding identical; plan_id {3}" -f $sumBytes, $fullBytes, @($sum.data.response_omissions).Count, $p.plan_id)
                }
                else { Out-Case 3 'fail' ("mode={0} summary={1} B full={2} B binding_identical={3} plan_id {4} vs {5}" -f $sum.data.response_mode, $sumBytes, $fullBytes, $sameBinding, $sum.data.plan_id, $p.plan_id) }
            }
            else { Out-Case 3 'fail' ('summary: ' + (Short $sum)) }

            # #2/#17 - apply by plan_id: rehearse, then apply with the rehearsal's tokens
            $base = @{ target_document = $doc; instance_id = $instance; requirement_set = $set; plan_id = [string]$p.plan_id }
            $dry = $base.Clone(); $dry['dry_run'] = $true
            $d = & $Ctx.Call 'horizun_apply_cad_plan' $dry
            if ($d.isError -or -not $d.data -or -not $d.data.rehearsal) { Out-Case 4 'fail' ('rehearsal: ' + (Short $d)) }
            else {
                $go = $base.Clone(); $go['dry_run'] = $false; $go['confirmation_tokens'] = $d.data.rehearsal.tokens_by_key
                $go['idempotency_key'] = "live-write-cps-apply-$tag"
                $ap = & $Ctx.Call 'horizun_apply_cad_plan' $go
                if ($ap.data) {
                    foreach ($st in @($ap.data.stages)) { foreach ($r in @($st.rows)) { if ($r.element_id) { $created.Insert(0, [long]$r.element_id) } } }
                }
                if ($ap.data -and [int]$ap.data.created_verified -eq $rows.Count -and [int]$ap.data.stages_failed -eq 0 -and $rows.Count -ge 2) {
                    Out-Case 4 'pass' ("{0} wall(s) created and verified at the planned base Z through plan_id; provenance on {1}" -f $ap.data.created_verified, $ap.data.provenance_written)
                }
                else { Out-Case 4 'fail' ("planned {0}, created_verified {1}: {2}" -f $rows.Count, $(if ($ap.data) { $ap.data.created_verified } else { 'n/a' }), (Short $ap)) }
            }
        }

        # ---- #4: a TwoLevelsBased column type, when the document has one ----------------------
        $colType = $null
        $types = & $Ctx.Call 'horizun_query_model' @{ target_document = $doc; categories = @('OST_StructuralColumns'); include_types = $true; include_links = $false; max_rows = 50 }
        if ($types.data) {
            # A loaded column TYPE reads as "Family: Type", the label the requirement set names it by.
            $colType = @(@($types.data.rows) | Where-Object { $_.is_element_type -eq $true -and $_.family -and $_.name } |
                ForEach-Object { [string]$_.family + ': ' + [string]$_.name })[0]
        }
        if (-not $colType -or -not $levelB -or -not $levelA) { Out-Case 5 'unverified' 'the document holds no structural column type to check, or the levels were not staged' }
        else {
            $colSet = @{
                schema = 'horizun.cad-requirements/1'
                requirement_set = @{ id = 'hz-live-columns'; version = '1.0.0'; title = 'Live probe: column top' }
                source = @{ units = 'millimeter' }
                tolerances = @{ point_mm = 1.0; gap_mm = 25.0; angle_degrees = 2.0; arc_sagitta_mm = 5.0 }
                rules = @(@{ id = 'columns'; layers = @('S-COLS*'); produces = 'structural_column'; category = 'OST_StructuralColumns'
                             family_type = $colType; level = "HZ_CPS_A_$tag"; geometry = @{ from = 'closed_loops' } })
            }
            $without = & $Ctx.Call 'horizun_plan_from_cad' @{ target_document = $doc; requirement_set = $colSet; catalog_check_only = $true }
            $row0 = if ($without.data) { @($without.data.rules)[0] } else { $null }
            if (-not $row0) { Out-Case 5 'fail' ('catalog check: ' + (Short $without)) }
            elseif ([string]$row0.placement_type -ne 'TwoLevelsBased') { Out-Case 5 'unverified' ("'{0}' is {1}, not TwoLevelsBased" -f $colType, $row0.placement_type) }
            else {
                $colSet.rules[0]['top_level'] = $levelBName
                $with = & $Ctx.Call 'horizun_plan_from_cad' @{ target_document = $doc; requirement_set = $colSet; catalog_check_only = $true }
                $row1 = if ($with.data) { @($with.data.rules)[0] } else { $null }
                $named = ([string]($row0.problems -join ' ')) -match 'top_level'
                if ($row0.verdict -eq 'refused' -and $named -and $row1 -and $row1.verdict -ne 'refused') {
                    Out-Case 5 'pass' ("'{0}': refused without a top ({1}); {2} with top_level '{3}'" -f $colType, ($row0.problems -join '; '), $row1.verdict, $levelBName)
                }
                else { Out-Case 5 'fail' ("without: {0} [{1}]; with: {2} [{3}]" -f $row0.verdict, ($row0.problems -join '; '), $row1.verdict, ($row1.problems -join '; ')) }
            }
        }

        # ---- cleanup: what was built, the link, the plans, then the levels ---------------------
        foreach ($id in $levels) { $created.Add($id) }
        if ($created.Count -eq 0) { Out-Case 6 'unverified' 'nothing was created' }
        else {
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created.ToArray()); id_cap = 200 } 'cps-cleanup'
            if (Applied $del) { Out-Case 6 'pass' ('deleted ' + ($created.ToArray() -join ',')) }
            else { Out-Case 6 'fail' ('left behind ' + ($created.ToArray() -join ',') + ': ' + (Short $del.answer)) }
        }
        return $cases.ToArray()
    }
}
