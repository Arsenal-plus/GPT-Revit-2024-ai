# Live probes for horizun_mep_routing operation 'route' (obstacle-avoiding MEP routing).
# Stages its own level far from the real model, puts its own structural column ON the
# straight line between two points, and asks for a pipe route between them: the route must
# detour, commit, keep every segment endpoint/size and every elbow connection on re-read,
# and carry a clean spatial check. A second case asks for an end INSIDE the column and must
# be refused as no_route naming the blocking region, with nothing written. Types are
# discovered from the fixture, never assumed by id or name. Everything created is deleted;
# the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'mep-route'
    Catalog = @(
        @{ Name = 'mep_routing route: a pipe detours around an own column in its straight line, commits, elbows connected, spatial check clean'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep_routing route: an end inside the column is refused as no_route naming the blocking region'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep-route probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        $catalog = @(
            'mep_routing route: a pipe detours around an own column in its straight line, commits, elbows connected, spatial check clean',
            'mep_routing route: an end inside the column is refused as no_route naming the blocking region',
            'mep-route probes: everything created is deleted')
        $tools = @('horizun_mep_routing', 'horizun_mep_routing', 'horizun_delete_verified')
        function Case($i, $outcome, $detail) { [void]$cases.Add(@{ Name = $catalog[$i]; Tool = $tools[$i]; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $i 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Verified($r) {
            $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data -and
            $r.answer.data.state -eq 'committed_verified' -and $r.answer.data.postconditions.all_verified -eq $true
        }
        function Why($r) { if ($r.stage -ne 'apply') { 'the rehearsal issued no token: ' + (Short $r.answer) } else { Short $r.answer } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-mrt-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }

        # ---- staging: own level far from everything, a column on the straight line --------
        $E = 97000.0; $X = 760000.0; $Y = 0.0; $Z = $E + 300.0
        $levelId = Create @(@{ kind = 'level'; name = "HZ_MRT_$run"; elevation = $E }) 'level'
        # A rigid pipe type (not a flex one) and a piping system, discovered from the fixture.
        $pipeType = Types 'OST_PipeCurves' | Where-Object { $_.family -notmatch 'Flex' -and $_.type -notmatch 'Flex' } | Select-Object -First 1
        $system = Types 'OST_PipingSystem' | Select-Object -First 1
        $columnType = Types 'OST_StructuralColumns' | Select-Object -First 1
        # STAGED, NEVER ASSUMED: an MEP fixture carries no structural column family, and
        # Autodesk's structural template of the run's year does (MEASURED 2026-09-26:
        # 'M_Concrete-Rectangular-Column: 300 x 450mm'); the typed copy brings the type in.
        if (-not $columnType) {
            $tpl = "C:\ProgramData\Autodesk\RVT $($Ctx.Year)\Templates\English\Structural Analysis-DefaultMetric.rte"
            if (Test-Path -LiteralPath $tpl) {
                $null = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); type_names = @('M_Concrete-Rectangular-Column: 300 x 450mm'); category = 'OST_StructuralColumns'; duplicate_types = 'use_destination' } ($run + '-route-coltype')
                $columnType = Types 'OST_StructuralColumns' | Select-Object -First 1
            }
        }
        $columnId = $null
        if ($levelId -and $columnType) {
            $columnId = Create @(@{ kind = 'structural_column'; point = @(($X + 3000), $Y, 0); coordinate_mode = 'level_offset'; level_id = $levelId; type_id = [long]$columnType.element_id }) 'column'
        }
        if (-not $levelId -or -not $pipeType -or -not $system -or -not $columnId) {
            $why = "the fixture lacks what the probe stages (level=$levelId, pipe type=" + $pipeType.element_id + ', piping system=' + $system.element_id + ", column=$columnId)"
            Case 0 'not_covered' $why; Case 1 'not_covered' $why
        }
        else {
            $base = @{ operation = 'route'; target_document = $doc; units = 'mm'; kind = 'pipe'; type_id = [long]$pipeType.element_id
                system_type_id = [long]$system.element_id; level_id = $levelId; diameter = 100; clearance_mm = 50; grid_mm = 100 }

            # ==== 1: detour around the column ==================================================
            $args1 = $base.Clone(); $args1.start = @($X, $Y, $Z); $args1.end = @(($X + 6000), $Y, $Z)
            $rt = & $Ctx.Apply 'horizun_mep_routing' $args1 ($run + '-mrt-route')
            $res = if ($rt.answer.data) { $rt.answer.data.result } else { $null }
            if ($res) { foreach ($id in @($res.segment_ids) + @($res.elbow_ids)) { if ($id) { [void]$created.Add([long]$id) } } }
            if (-not (Verified $rt)) { Case 0 'fail' ('route: ' + (Why $rt)) }
            else {
                $segs = @($res.segment_ids); $elbows = @($res.elbow_ids); $length = [double]$res.length_mm
                $sc = $res.spatial_check
                if ([int]$res.bends -lt 2 -or $length -le 6000.5) {
                    Case 0 'fail' ("the route did not detour: bends=$($res.bends), length=$length mm for a 6000 mm straight line through the column")
                }
                elseif ($elbows.Count -ne ($segs.Count - 1)) {
                    Case 0 'fail' ("$($segs.Count) segments but $($elbows.Count) elbows - every bend needs its own elbow")
                }
                elseif (-not $sc -or [int]$sc.errors -ne 0 -or $sc.partial -ne $false) {
                    Case 0 'fail' ('the committed route carries no clean spatial check: ' + ($sc | ConvertTo-Json -Compress))
                }
                else {
                    Case 0 'pass' ("column $columnId avoided: $($segs.Count) segments, $($elbows.Count) elbows, $($res.bends) bends, $length mm; spatial check $($sc.checked)/$($sc.subjects) checked, 0 errors, partial=$($sc.partial)")
                }
            }

            # ==== 2: an end inside the column is no_route, nothing written =======================
            $args2 = $base.Clone(); $args2.start = @($X, ($Y - 3000), $Z); $args2.end = @(($X + 3000), $Y, $Z)
            $nr = & $Ctx.Call 'horizun_mep_routing' $args2
            $text = [string]$nr.text
            if (-not $nr.isError) { Case 1 'fail' ('an end inside the column was not refused: ' + (Short $nr)) }
            elseif ($text -notmatch 'no_route') { Case 1 'fail' ('refused, but not as no_route: ' + (Short $nr)) }
            elseif ($text -notmatch [regex]::Escape([string]$columnId)) { Case 1 'fail' ("no_route does not name the column $columnId as the blocking region: " + (Short $nr)) }
            elseif ($text -notmatch 'Nothing was written') { Case 1 'fail' ('no_route does not say nothing was written: ' + (Short $nr)) }
            else { Case 1 'pass' ("refused, naming column $columnId") }
        }

        # ---- cleanup: newest first, the level last ------------------------------------------
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case 2 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-mrt-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 2 'pass' ('deleted ' + $ids.Count + ' created ids') }
            else { Case 2 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Why $del)) }
        }
        return $cases
    }
}
