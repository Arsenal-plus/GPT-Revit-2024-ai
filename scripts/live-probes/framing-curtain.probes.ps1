# Live probes for horizun_framing's CURTAIN method (spec.wall.method / spec.ceiling.method =
# 'curtain') and horizun_manage_curtain's read of a sloped glazing roof. Everything stands on
# the module's own level at X = 1,170,000 mm, far from the real model: two own Basic walls (one
# with ONE door - the trimmed-placeholder case - and one with none - the deleted-carrier case)
# and an own ceiling under an own floor (the hangers must reach THAT floor). Types are STAGED BY
# NAME from the year's English\DefaultMetric.rte with horizun_copy_between_documents, never "the
# first one": a Curtain Wall type, two Basic walls, a door, the Sloped Glazing roof type, a floor
# and a compound ceiling.
# NOT YET STAGED: the probe's OWN duplicated types with a 406.4 mm Fixed Distance grid and a
# 41.3 x 92.1 mm rectangular mullion (horizun_manage_system_types duplicate +
# horizun_write_params_verified on SPACING_LAYOUT_VERT / SPACING_LENGTH_VERT / AUTO_MULLION_*).
# Until then the template type's own layout is what the grid checks read, and each case says
# whether the fixed-distance spacing check ran or the grid was only counted.
# MEASURED LIVE BY THESE CASES, not assumed by the code: the numeric Fixed Distance value of
# SPACING_LAYOUT_VERT / SPACING_LAYOUT_1, the reference CURTAINGRID_ANGLE_1 is measured from on a
# flat roof (grid_direction), whether the trimmed carrier keeps its door where it was, the
# carrier's delete cascade, and where ModelEditRunner puts horizun_manage_curtain's read result.
# Everything created is deleted at the end (framing by operation=remove, which restores the
# carriers; staging by horizun_delete_verified mode='ids'); the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'framing-curtain'
    Catalog = @(
        @{ Name = 'framing curtain wall: the rehearsal plans segments and a header around one door, the carrier trimmed to the placeholder'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain wall: apply verified, grid re-read, the door still hosted by the trimmed placeholder'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain wall: a second apply of the same spec is already_applied'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain wall: a wall with no opening is replaced, its carrier deleted with the cascade as measured'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain remove: the pieces go and both carriers are restored, the deleted one under a new id'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain ceiling: the rehearsal plans two sloped glazing layers and hangers up to the floor above'; Tool = 'horizun_framing' }
        @{ Name = 'framing curtain ceiling: apply verified, layer planes, footprints and grids re-read, hangers reach the staged floor'; Tool = 'horizun_framing' }
        @{ Name = 'manage_curtain read: a sloped glazing layer''s grid is read with its angles'; Tool = 'horizun_manage_curtain' }
        @{ Name = 'framing curtain probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'framing curtain wall: the rehearsal plans segments and a header around one door, the carrier trimmed to the placeholder',
            'framing curtain wall: apply verified, grid re-read, the door still hosted by the trimmed placeholder',
            'framing curtain wall: a second apply of the same spec is already_applied',
            'framing curtain wall: a wall with no opening is replaced, its carrier deleted with the cascade as measured',
            'framing curtain remove: the pieces go and both carriers are restored, the deleted one under a new id',
            'framing curtain ceiling: the rehearsal plans two sloped glazing layers and hangers up to the floor above',
            'framing curtain ceiling: apply verified, layer planes, footprints and grids re-read, hangers reach the staged floor',
            'manage_curtain read: a sloped glazing layer''s grid is read with its angles',
            'framing curtain probes: everything created is deleted')
        # Tool names apart from every case-insensitive variable below ($T is not $t, $McTool is not $mr).
        $T = 'horizun_framing'; $McTool = 'horizun_manage_curtain'; $DeleteTool = 'horizun_delete_verified'
        function ToolOf($name) { if ($name -like 'manage_curtain*') { $McTool } elseif ($name -like 'framing curtain probes:*') { $DeleteTool } else { $T } }
        if ($Ctx.WriteGate) {
            foreach ($name in $catalog) { Case $name (ToolOf $name) 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $s = [string]$a.text; if ($s.Length -gt 400) { $s.Substring(0, 400) } else { $s } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-frc-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }
        $tplRoot = if ($Ctx.TemplateRoot) { [string]$Ctx.TemplateRoot } else { 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates' }
        function FindType($category, $typeName, $familyName) {
            @(Types $category | Where-Object { [string]$_.type -eq $typeName -and [string]$_.family -eq $familyName }) | Select-Object -First 1
        }
        # The first of $typeNames the document has; else each copied from the template in turn.
        function Bring($category, $typeNames, $familyName, $key) {
            foreach ($tn in @($typeNames)) { $have = FindType $category $tn $familyName; if ($have) { return $have } }
            $tpl = Join-Path $tplRoot 'English\DefaultMetric.rte'
            if (-not (Test-Path -LiteralPath $tpl)) { return $null }
            $k = 0
            foreach ($tn in @($typeNames)) {
                $k++
                $null = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); category = $category
                        type_names = @($familyName + ': ' + $tn); duplicate_types = 'use_destination' } ($run + '-frc-' + $key + $k)
                $have = FindType $category $tn $familyName
                if ($have) { return $have }
            }
            return $null
        }

        # ---- staging: own level, a wall with one door, a wall with none -----------------------
        $E = 98000.0; $X = 1170000.0; $Y = 0.0
        $curtainType = Bring 'OST_Walls' @('Curtain Wall 1', 'Exterior Glazing', 'Storefront') 'Curtain Wall' 'curtain'
        $carrierType = Bring 'OST_Walls' @('Generic - 200mm') 'Basic Wall' 'carrier'
        $placeholderType = Bring 'OST_Walls' @('Generic - 150mm', 'Generic - 90mm Brick') 'Basic Wall' 'placeholder'
        $doorType = Bring 'OST_Doors' @('0915 x 2134mm') 'M_Single-Flush' 'door'
        $level = Create @(@{ kind = 'level'; name = "HZ_FRC_$run"; elevation = $E }) 'level'
        $wall1 = $null; $wall2 = $null; $door = $null
        if ($level -and $carrierType) {
            $wall1 = Create @(@{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $level; type_id = $carrierType.element_id; height = 3000 }) 'wall1'
            $wall2 = Create @(@{ kind = 'wall'; start = @($X, ($Y + 8000), $E); end = @(($X + 3000), ($Y + 8000), $E); level_id = $level; type_id = $carrierType.element_id; height = 3000 }) 'wall2'
        }
        if ($wall1 -and $doorType) { $door = Create @(@{ kind = 'family_instance'; type_id = $doorType.element_id; host_id = $wall1; point = @(($X + 2500), $Y, $E); coordinate_mode = 'absolute'; level_id = $level }) 'door' }
        function WallSpec { @{ wall = @{ method = 'curtain'; curtain_type_id = [long]$curtainType.element_id; placeholder_type_id = [long]$placeholderType.element_id; multi_opening = 'refuse' } } }
        $ready = $wall1 -and $door -and $curtainType -and $placeholderType
        $why = "staging incomplete: wall $wall1, door $door, curtain type '$($curtainType.element_id)', placeholder type '$($placeholderType.element_id)'"
        $w1Args = @{ operation = 'wall'; target_document = $doc; element_ids = @($wall1); spec = (WallSpec) }
        function Count($rows, $role) { @($rows | Where-Object { $_.role -eq $role }).Count }

        # ==== 1: rehearsal of the one-door wall ================================================
        if (-not $ready) { Case $catalog[0] $T 'not_covered' $why }
        else {
            $d = & $Ctx.Call $T ($w1Args + @{ dry_run = $true })
            $src = $null
            if ($d.data) { $src = @($d.data.plan.sources)[0] }
            if ($d.isError -or -not $src) { Case $catalog[0] $T 'fail' ('rehearsal: ' + (Short $d)) }
            else {
                $problems = @()
                if ([string]$src.method -ne 'curtain') { $problems += "method '$($src.method)'" }
                if (@($src.openings).Count -ne 1) { $problems += "read $(@($src.openings).Count) openings, expected 1" }
                if ((Count $src.pieces 'curtain_segment') -ne 2) { $problems += "$(Count $src.pieces 'curtain_segment') segments, expected 2" }
                if ((Count $src.pieces 'curtain_header') -ne 1) { $problems += "$(Count $src.pieces 'curtain_header') headers, expected 1" }
                if ((Count $src.pieces 'curtain_sill') -ne 0) { $problems += 'a sill planned under a door' }
                if ([string]$src.carrier.action -ne 'trim' -or [long]$src.carrier.type_id -ne [long]$placeholderType.element_id) { $problems += "carrier '$($src.carrier.action)' to type $($src.carrier.type_id)" }
                if ($problems.Count -gt 0) { Case $catalog[0] $T 'fail' ($problems -join '; ') }
                else { Case $catalog[0] $T 'pass' ("$(@($src.pieces).Count) pieces, carrier trimmed to " + (@($src.carrier.span) -join '..') + " mm with type $($src.carrier.type_id), core offset $($src.core_offset_mm) mm") }
            }
        }

        # ==== 2: apply, 3: idempotent apply ====================================================
        $applied = $false
        if (-not $ready) { Case $catalog[1] $T 'not_covered' $why; Case $catalog[2] $T 'not_covered' $why }
        else {
            $a = & $Ctx.Apply $T $w1Args ($run + '-frc-apply')
            $ev = $null
            if ($a.answer.data) { $ev = @($a.answer.data.evidence.sources)[0] }
            if ($a.stage -ne 'apply' -or $a.answer.isError -or $a.answer.data.postconditions.all_verified -ne $true -or -not $ev) { Case $catalog[1] $T 'fail' ('apply: ' + (Short $a.answer)) }
            elseif ([string]$a.answer.data.application.state -ne 'verified_applied') { Case $catalog[1] $T 'fail' "application.state '$($a.answer.data.application.state)', expected verified_applied" }
            elseif ([string]$ev.carrier.action -ne 'trim' -or $ev.carrier.type_ok -ne $true -or [int]$ev.carrier.inserts_checked -ne 1 -or [int]$ev.carrier.inserts_changed -ne 0) {
                Case $catalog[1] $T 'fail' ('carrier: ' + ($ev.carrier | ConvertTo-Json -Compress -Depth 4)) }
            else {
                $applied = $true
                $grids = @($ev.pieces | ForEach-Object { $_.grid })
                $fixed = @($grids | Where-Object { [int]$_.layout_vert -eq 1 }).Count
                Case $catalog[1] $T 'pass' ("$(@($ev.pieces).Count) pieces re-read; spacing checked (Fixed Distance) on $fixed of $($grids.Count), layouts " +
                    (($grids | ForEach-Object { "$($_.layout_vert)=$($_.layout_vert_text)" } | Sort-Object -Unique) -join ',') + '; vertical lines ' + (($grids | ForEach-Object { $_.vertical_lines }) -join ',') +
                    "; carrier line off $($ev.carrier.curve_deviation_mm) mm, its door unchanged and still hosted")
            }
            if (-not $applied) { Case $catalog[2] $T 'not_covered' 'the first apply did not verify' }
            else {
                $b = & $Ctx.Apply $T $w1Args ($run + '-frc-apply-again')
                if ($b.stage -eq 'apply' -and -not $b.answer.isError -and $b.answer.data.already_applied -eq $true -and $b.answer.data.postconditions.all_verified -eq $true -and [string]$b.answer.data.application.state -eq 'no_op') {
                    Case $catalog[2] $T 'pass' 'already_applied (application no_op), the pieces re-read against the record they carry' }
                else { Case $catalog[2] $T 'fail' ('second apply: ' + (Short $b.answer)) }
            }
        }

        # ==== 4: the wall with no opening is replaced ==========================================
        $replaced = $false
        if (-not ($wall2 -and $curtainType -and $placeholderType)) { Case $catalog[3] $T 'not_covered' "staging incomplete: wall $wall2, curtain type '$($curtainType.element_id)'" }
        else {
            $w2Args = @{ operation = 'wall'; target_document = $doc; element_ids = @($wall2); spec = (WallSpec) }
            $d2 = & $Ctx.Call $T ($w2Args + @{ dry_run = $true })
            $s2 = $null; $a2 = $null; $ev2 = $null
            if ($d2.data) { $s2 = @($d2.data.plan.sources)[0] }
            if ($s2 -and [string]$s2.carrier.action -eq 'delete') { $a2 = & $Ctx.Apply $T $w2Args ($run + '-frc-apply2') }
            if ($a2 -and $a2.answer.data) { $ev2 = @($a2.answer.data.evidence.sources)[0] }
            if (-not $s2 -or [string]$s2.carrier.action -ne 'delete') { Case $catalog[3] $T 'fail' ('rehearsal: ' + $(if ($s2) { "carrier action '$($s2.carrier.action)', expected delete" } else { Short $d2 })) }
            elseif ($a2.stage -ne 'apply' -or $a2.answer.isError -or $a2.answer.data.postconditions.all_verified -ne $true -or -not $ev2 -or $ev2.carrier.deleted -ne $true) { Case $catalog[3] $T 'fail' ('apply: ' + (Short $a2.answer)) }
            else {
                $replaced = $true
                $created.Remove([long]$wall2)   # gone: the cleanup must not name it
                Case $catalog[3] $T 'pass' ("carrier $wall2 deleted after $(@($ev2.pieces).Count) piece(s); cascade measured $(@($ev2.carrier.deleted_with_it_measured).Count), taken $(@($ev2.carrier.deleted_with_it).Count) (carrier_cascade_as_measured inside all_verified)")
            }
        }

        # ==== 5: remove restores both carriers =================================================
        $restoredOk = $true
        $removeIds = @()
        if ($applied) { $removeIds += $wall1 }
        if ($replaced) { $removeIds += $wall2 }
        if ($removeIds.Count -eq 0) { Case $catalog[4] $T 'not_covered' 'nothing was applied' }
        else {
            $rm = & $Ctx.Apply $T @{ operation = 'remove'; target_document = $doc; element_ids = $removeIds } ($run + '-frc-remove')
            $rows = @()
            if ($rm.answer.data) { $rows = @($rm.answer.data.evidence.carrier_restores) }
            foreach ($row in $rows) { if ($row.recreated_with_new_id -eq $true -and $row.wall_id) { [void]$created.Add([long]$row.wall_id) } }
            $problems = @()
            if ($rm.stage -ne 'apply' -or $rm.answer.isError -or $rm.answer.data.postconditions.all_verified -ne $true) { $problems += 'remove: ' + (Short $rm.answer) }
            else {
                if ($rows.Count -ne $removeIds.Count) { $problems += "$($rows.Count) restore row(s) for $($removeIds.Count) carrier(s)" }
                foreach ($row in $rows) { if ($row.restored -ne $true) { $problems += "carrier $($row.carrier_id) not restored: $($row.not_restored_because) $(@($row.disagrees) -join ',')" } }
                $w1row = @($rows | Where-Object { [long]$_.carrier_id -eq [long]$wall1 }) | Select-Object -First 1
                if ($applied -and (-not $w1row -or [int]$w1row.inserts_changed -ne 0)) { $problems += 'the trimmed carrier did not get its door back where it was' }
                if ($replaced -and @($rows | Where-Object { $_.recreated_with_new_id -eq $true }).Count -ne 1) { $problems += 'the deleted carrier was not recreated' }
            }
            $restoredOk = ($problems.Count -eq 0)
            if ($restoredOk) { Case $catalog[4] $T 'pass' ("$(@($rm.answer.data.evidence.removed_ids).Count) piece(s) removed; restored " + (($rows | ForEach-Object { "$($_.carrier_id)->$($_.wall_id) (line off $($_.line_deviation_mm) mm)" }) -join ', ')) }
            else { Case $catalog[4] $T 'fail' ($problems -join '; ') }
        }

        # ==== 6, 7: ceiling as two sloped glazing layers with hangers ==========================
        # The ceiling (4800 x 3600) hangs under an own floor whose TOP is at level + 3000; a
        # profile's z is the element's height (the ceiling's underside offset, the floor's top).
        $glazingType = Bring 'OST_Roofs' @('Sloped Glazing') 'Sloped Glazing' 'glazing'
        $floorType = Bring 'OST_Floors' @('Generic 300mm', 'Generic 150mm') 'Floor' 'floortype'
        $ceilingType = Bring 'OST_Ceilings' @('600 x 600mm Grid') 'Compound Ceiling' 'ceilingtype'
        $CZ = $E + 2400; $FZ = $E + 3000; $CX = $X + 20000; $CY = $Y
        $floor = $null; $ceiling = $null
        if ($level -and $floorType) {
            $floor = Create @(@{ kind = 'floor'; level_id = $level; type_id = $floorType.element_id
                                 profile = @(, @(@(($CX - 600), ($CY - 600), $FZ), @(($CX + 5400), ($CY - 600), $FZ), @(($CX + 5400), ($CY + 4200), $FZ), @(($CX - 600), ($CY + 4200), $FZ))) }) 'floor'
        }
        if ($level -and $ceilingType) {
            $ceiling = Create @(@{ kind = 'ceiling'; level_id = $level; type_id = $ceilingType.element_id
                                   profile = @(, @(@($CX, $CY, $CZ), @(($CX + 4800), $CY, $CZ), @(($CX + 4800), ($CY + 3600), $CZ), @($CX, ($CY + 3600), $CZ))) }) 'ceiling'
        }
        $cSpec = @{ ceiling = @{ method = 'curtain'
            layers = @(@{ type_id = [long]$glazingType.element_id; offset_mm = 0; angle_deg = 0 }, @{ type_id = [long]$glazingType.element_id; offset_mm = 30; angle_deg = 90 })
            hanger = @{ type_id = [long]$curtainType.element_id; spacing_mm = 1200; max_length_mm = 3000; attach = 'structure_above' } } }
        $cArgs = @{ operation = 'ceiling'; target_document = $doc; element_ids = @($ceiling); spec = $cSpec }
        $cWhy = "staging incomplete: floor $floor, ceiling $ceiling, sloped glazing type '$($glazingType.element_id)', curtain type '$($curtainType.element_id)'"
        $cCommitted = $false; $layerId = $null
        if (-not ($floor -and $ceiling -and $glazingType -and $curtainType)) { Case $catalog[5] $T 'not_covered' $cWhy; Case $catalog[6] $T 'not_covered' $cWhy }
        else {
            $cd = & $Ctx.Call $T ($cArgs + @{ dry_run = $true })
            $cs = $null
            if ($cd.data) { $cs = @($cd.data.plan.sources)[0] }
            if ($cd.isError -or -not $cs) { Case $catalog[5] $T 'fail' ('rehearsal: ' + (Short $cd)); Case $catalog[6] $T 'not_covered' 'the rehearsal failed' }
            else {
                $problems = @()
                if ([string]$cs.method -ne 'curtain') { $problems += "method '$($cs.method)'" }
                $planes = @($cs.layers | ForEach-Object { [double]$_.plane_mm })
                if ($planes.Count -ne 2) { $problems += "$($planes.Count) layers, expected 2" }
                elseif ([math]::Abs(($planes[1] - $planes[0]) - 30) -gt 0.2) { $problems += 'layer planes ' + ($planes -join ',') + ' are not 30 mm apart' }
                if (@($cs.hangers).Count -eq 0) { $problems += 'no hanger planned' }
                if (@($cs.not_built).Count -ne 0) { $problems += "$(@($cs.not_built).Count) hanger line(s) not built under the staged floor" }
                $off = @($cs.hangers | Where-Object { [double]$_.top_mm -le [double]$_.base_mm -or [double]$_.top_mm -gt ($FZ + 1) -or [double]$_.top_mm -lt ($FZ - 1000) })
                if ($off.Count -gt 0) { $problems += "$($off.Count) hanger(s) end off the floor's underside (floor top $FZ)" }
                if ($problems.Count -gt 0) { Case $catalog[5] $T 'fail' ($problems -join '; ') }
                else { Case $catalog[5] $T 'pass' ('2 layers at ' + ($planes -join ',') + " mm, $(@($cs.hangers).Count) hanger(s) from $($cs.hanger_base_mm) to " + ((@($cs.hangers) | ForEach-Object { $_.top_mm } | Sort-Object -Unique | Select-Object -First 3) -join ',') + ' mm') }

                $ca = & $Ctx.Apply $T $cArgs ($run + '-frc-ceiling')
                $cCommitted = ($ca.stage -eq 'apply' -and -not $ca.answer.isError -and $ca.answer.data.transaction_status -eq 'Committed')
                $cev = $null
                if ($ca.answer.data) { $cev = @($ca.answer.data.evidence.sources)[0] }
                if (-not $cCommitted -or $ca.answer.data.postconditions.all_verified -ne $true -or -not $cev) { Case $catalog[6] $T 'fail' ('apply: ' + (Short $ca.answer)) }
                else {
                    $layers = @($cev.pieces | Where-Object { $_.role -eq 'curtain_layer' })
                    $layerId = @($layers | ForEach-Object { $_.id }) | Select-Object -First 1
                    $recheck = $ca.answer.data.evidence.hanger_recheck
                    $problems = @()
                    if ($layers.Count -ne 2) { $problems += "$($layers.Count) layer(s) re-read, expected 2" }
                    if (@($recheck.not_at_support).Count -ne 0) { $problems += "$(@($recheck.not_at_support).Count) hanger(s) not at the support" }
                    if ([int]$recheck.stations_checked -le 0) { $problems += 'no hanger station was re-cast' }
                    if ($problems.Count -gt 0) { Case $catalog[6] $T 'fail' ($problems -join '; ') }
                    else {
                        Case $catalog[6] $T 'pass' ("$(@($cev.pieces).Count) pieces re-read; layer planes off " + (($layers | ForEach-Object { $_.plane_deviation_mm }) -join ',') + ' mm, footprints off ' +
                            (($layers | ForEach-Object { $_.footprint_deviation_mm }) -join ',') + ' mm, grid 1 at ' + (($layers | ForEach-Object { $_.grid.grid1.direction_deg }) -join ',') +
                            " deg; $($recheck.stations_checked) hanger station(s) at the floor, max gap $($recheck.max_gap_mm) mm")
                    }
                }
            }
        }

        # ==== 8: manage_curtain reads a layer's grid ===========================================
        if (-not $layerId) { Case $catalog[7] $McTool 'not_covered' 'no sloped glazing layer was committed' }
        else {
            $mr = & $Ctx.Call $McTool @{ operation = 'read'; target_document = $doc; element_id = [long]$layerId; grid_index = 0 }
            # Where ModelEditRunner puts a read result is held against the first live run.
            $rd = $null
            foreach ($cand in @($mr.data, $mr.data.read, $mr.data.result)) { if ($cand -and $cand.counts) { $rd = $cand; break } }
            if ($mr.isError -or -not $rd) { Case $catalog[7] $McTool 'fail' ('read: ' + (Short $mr)) }
            elseif ([string]$rd.host_kind -ne 'FootPrintRoof' -or $null -eq $rd.grid1_angle_deg) { Case $catalog[7] $McTool 'fail' "host_kind '$($rd.host_kind)', grid1_angle_deg '$($rd.grid1_angle_deg)' $($rd.grid_angles)" }
            else { Case $catalog[7] $McTool 'pass' ("u $($rd.counts.u_lines) / v $($rd.counts.v_lines) grid lines, $($rd.counts.mullions) mullions, $($rd.counts.panels) panels; grid 1 at $($rd.grid1_angle_deg) deg, grid 2 at $($rd.grid2_angle_deg) deg") }
        }

        # ==== 9: cleanup ======================================================================
        # The ceiling's layers and hangers are not hosted by it: remove runs before the delete.
        $notes = @(); $framingGone = $restoredOk
        if (-not $restoredOk) { $notes += 'the wall pieces were not removed and their carriers restored' }
        if ($cCommitted) {
            $crm = & $Ctx.Apply $T @{ operation = 'remove'; target_document = $doc; element_ids = @($ceiling) } ($run + '-frc-ceiling-remove')
            if ($crm.stage -eq 'apply' -and -not $crm.answer.isError -and $crm.answer.data.postconditions.all_verified -eq $true) { $notes += "ceiling framing removed ($(@($crm.answer.data.evidence.removed_ids).Count) element(s))" }
            else { $framingGone = $false; $notes += 'ceiling remove: ' + (Short $crm.answer) }
        }
        $ids = @($created | Sort-Object -Descending -Unique)
        if ($ids.Count -eq 0 -and -not $cCommitted) { Case $catalog[8] $DeleteTool 'not_covered' 'nothing was created' }
        else {
            $delOk = $true
            if ($ids.Count -gt 0) {
                $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids } ($run + '-frc-cleanup')
                $delOk = ($del.stage -eq 'apply' -and -not $del.answer.isError)
                if ($delOk) { $notes += "$($ids.Count) staged element(s) deleted" } else { $notes += 'cleanup: ' + (Short $del.answer) }
            }
            if ($delOk -and $framingGone) { Case $catalog[8] $DeleteTool 'pass' ($notes -join '; ') } else { Case $catalog[8] $DeleteTool 'fail' ($notes -join '; ') }
        }
        return $cases
    }
}
