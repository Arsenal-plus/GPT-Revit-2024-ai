# Live probe module: horizun_resolve_clash propose_opening / apply_opening (see README.md).
# Stages an OWN level far above everything, and on it three crossings the move-based
# resolver cannot fix: a horizontal pipe through an own wall, a vertical pipe through
# an own floor and a vertical pipe through an own beam. Records them in the ledger,
# proposes openings, applies the wall and floor cuts (each re-read: the opening exists,
# a clearance envelope around the run meets no host material, the run no longer meets the
# host solid and the finding is only marked opening_requested - still open), checks that
# the beam cut is refused by name in propose AND in the apply rehearsal, deletes everything
# and closes the probe's own findings so the ledger does not keep pointing at them.
# allow_structural=true is the fixture's recorded approval: whether an own wall/floor is
# created structural depends on the template, and the refusal itself is unit-tested.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'sleeves-openings'
    Catalog = @(
        @{ Name = 'sleeves-openings: propose_opening plans a wall cut around an own pipe with the clearance'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'sleeves-openings: apply_opening cuts the wall and re-reads host_cleared on solids'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'sleeves-openings: the wall finding stays open with an opening_requested history entry'; Tool = 'horizun_coordination' }
        @{ Name = 'sleeves-openings: propose_opening plans a round floor cut around a vertical own pipe'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'sleeves-openings: apply_opening cuts the floor and re-reads host_cleared on solids'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'sleeves-openings: a beam cut is refused as member_cut_not_offered in propose and apply'; Tool = 'horizun_resolve_clash' }
        @{ Name = 'sleeves-openings: everything the probe created is deleted and its findings closed'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'sleeves-openings' } | Select-Object -First 1).Catalog
        function Case($i, $outcome, $detail) { [void]$cases.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { foreach ($i in 0..6) { Case $i 'not_covered' 'write tier closed' }; return $cases }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Json($o) { if ($null -eq $o) { 'null' } else { (($o | ConvertTo-Json -Depth 6 -Compress) -as [string]) } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q -or -not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-so-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $id = [long]@($r.answer.data.rows)[0].element_id
                [void]$created.Add($id)
                return $id
            }
            return $null
        }

        try {
            # ---- staging ----------------------------------------------------------------
            $E = 95000.0; $X = 640000.0; $Y = 0.0
            $pipeType = @(Types 'OST_PipeCurves' | Select-Object -First 1)[0]
            $system = @(Types 'OST_PipingSystem' | Select-Object -First 1)[0]
            $wallType = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') } | Select-Object -First 1)[0]
            $floorType = @(Types 'OST_Floors' | Select-Object -First 1)[0]
            $beamType = @(Types 'OST_StructuralFraming' | Select-Object -First 1)[0]
            # STAGED, NEVER ASSUMED: an MEP fixture carries no framing family; Autodesk's
            # structural template of the run's year does (MEASURED 2026-09-26: 'M_Concrete-
            # Rectangular Beam: 300 x 600mm'), brought in by the typed copy.
            if (-not $beamType) {
                $tpl = "C:\ProgramData\Autodesk\RVT $($Ctx.Year)\Templates\English\Structural Analysis-DefaultMetric.rte"
                if (Test-Path -LiteralPath $tpl) {
                    $null = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); type_names = @('M_Concrete-Rectangular Beam: 300 x 600mm'); category = 'OST_StructuralFraming'; duplicate_types = 'use_destination' } ($run + '-so-beamtype')
                    $beamType = @(Types 'OST_StructuralFraming' | Select-Object -First 1)[0]
                }
            }
            $level = Create @{ kind = 'level'; name = "HZ_SLEEVE_$run"; elevation = $E } 'level'
            function Pipe($s, $e, $d, $key) {
                if (-not $level -or -not $pipeType -or -not $system) { return $null }
                return Create @{ kind = 'pipe'; start = $s; end = $e; diameter = $d; level_id = $level; type_id = [long]$pipeType.element_id; system_type_id = [long]$system.element_id } $key
            }
            $wall = $null; $pipeW = $null; $floor = $null; $pipeF = $null; $beam = $null; $pipeB = $null
            if ($level -and $wallType) {
                $wall = Create @{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 4000), $Y, $E); level_id = $level; type_id = [long]$wallType.element_id; height = 3000 } 'wall'
                if ($wall) { $pipeW = Pipe @(($X + 2000), ($Y - 1500), ($E + 1500)) @(($X + 2000), ($Y + 1500), ($E + 1500)) 100 'pipe-wall' }
            }
            if ($level -and $floorType) {
                $fx = $X + 10000
                $floor = Create @{ kind = 'floor'; level_id = $level; type_id = [long]$floorType.element_id
                                   profile = @(,@(@($fx, $Y, $E), @(($fx + 4000), $Y, $E), @(($fx + 4000), ($Y + 4000), $E), @($fx, ($Y + 4000), $E))) } 'floor'
                if ($floor) { $pipeF = Pipe @(($fx + 2000), ($Y + 2000), ($E - 1000)) @(($fx + 2000), ($Y + 2000), ($E + 1000)) 100 'pipe-floor' }
            }
            if ($level -and $beamType) {
                # A VERTICAL pipe on the beam axis crosses whatever depth and justification the type has.
                $bx = $X + 20000
                $beam = Create @{ kind = 'structural_framing'; start = @($bx, $Y, ($E + 3000)); end = @(($bx + 4000), $Y, ($E + 3000)); level_id = $level; type_id = [long]$beamType.element_id } 'beam'
                if ($beam) { $pipeB = Pipe @(($bx + 2000), $Y, ($E + 1500)) @(($bx + 2000), $Y, ($E + 4500)) 50 'pipe-beam' }
            }

            # ---- ledger: one detection records the three crossings -----------------------
            $rows = @(); $actions = @()
            if ($pipeW -or $pipeF -or $pipeB) {
                $null = & $Ctx.Call 'horizun_clash' @{ categories_a = @('OST_PipeCurves'); categories_b = @('OST_Walls', 'OST_Floors', 'OST_StructuralFraming')
                                                      include_links = $false; record_findings = $true; max_results = 500 }
                $open = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; status = 'open'; max_rows = 500 }
                $ids = @($open.data.rows | ForEach-Object { [string]$_.finding_id })
                $mine = @($pipeW, $pipeF, $pipeB | Where-Object { $_ })
                for ($i = 0; $i -lt $ids.Count; $i += 50) {
                    $chunk = @($ids[$i..([Math]::Min($i + 49, $ids.Count - 1))])
                    $p = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'propose_opening'; target_document = $doc; finding_ids = $chunk; clearance_mm = 50; allow_structural = $true }
                    if (-not $p -or -not $p.data) { continue }
                    $rows += @($p.data.proposals | Where-Object { $_.mep_element_id -and ($mine -contains [long]$_.mep_element_id) })
                    if ($p.data.next_arguments) { $actions += @($p.data.next_arguments.proposals) }
                }
            }
            function RowFor($mep, $hostId) { @($rows | Where-Object { [long]$_.mep_element_id -eq $mep -and [long]$_.host_element_id -eq $hostId }) | Select-Object -First 1 }
            function ActionFor($fid) { @($actions | Where-Object { [string]$_.finding_id -eq $fid }) | Select-Object -First 1 }
            function ApplyCut($row, $key) {
                $act = ActionFor ([string]$row.finding_id)
                if (-not $act) { return @{ ok = $false; detail = 'propose returned no action for ' + $row.finding_id } }
                $ap = & $Ctx.Apply 'horizun_resolve_clash' @{ operation = 'apply_opening'; target_document = $doc; clearance_mm = 50; allow_structural = $true; proposals = @($act) } ($run + '-so-' + $key)
                $d = if ($ap.answer) { $ap.answer.data } else { $null }
                $made = if ($d) { @($d.created) | Select-Object -First 1 } else { $null }
                if ($made -and $made.created_element_id) { [void]$created.Add([long]$made.created_element_id) }
                $ok = $ap.stage -eq 'apply' -and -not $ap.answer.isError -and $d -and $d.postconditions.all_verified -eq $true -and
                      $made -and $made.host_cut -eq $true -and (@($d.findings_opening_requested) -contains [string]$row.finding_id) -and
                      @($d.findings_resolved_by_model).Count -eq 0
                $detail = if ($ok) { "$($made.kind) $($made.created_element_id) ($($made.placement)) on $($made.host_kind); verdict: $($d.verdict)" }
                          elseif ($ap.stage -ne 'apply') { 'the rehearsal issued no token: ' + (Short $ap.answer) } else { Short $ap.answer }
                return @{ ok = $ok; detail = $detail }
            }

            # ---- wall -----------------------------------------------------------------------
            if (-not $pipeW) { foreach ($i in 0..2) { Case $i 'not_covered' "no own wall and pipe could be staged in '$doc'" } }
            else {
                $rw = RowFor $pipeW $wall
                $okW = $rw -and $rw.status -eq 'proposed' -and $rw.host_kind -eq 'wall' -and $rw.route -eq 'wall_opening' -and
                       [double]$rw.opening_width_mm -ge 150 -and [double]$rw.opening_height_mm -ge 150 -and @($rw.crossing_point_mm).Count -eq 3
                if (-not $okW) {
                    Case 0 'fail' ('no wall_opening proposal for pipe ' + $pipeW + ': ' + (Json $rw))
                    Case 1 'unverified' 'no wall proposal to apply'; Case 2 'unverified' 'no wall proposal to apply'
                } else {
                    Case 0 'pass' ("finding $($rw.finding_id): $($rw.opening_width_mm) x $($rw.opening_height_mm) mm $($rw.shape) at " + (@($rw.crossing_point_mm) -join ','))
                    $aw = ApplyCut $rw 'apply-wall'
                    Case 1 $(if ($aw.ok) { 'pass' } else { 'fail' }) $aw.detail
                    if (-not $aw.ok) { Case 2 'unverified' 'the wall cut was not applied' }
                    else {
                        $all = & $Ctx.Call 'horizun_coordination' @{ operation = 'list'; max_rows = 500 }
                        $f = @($all.data.rows | Where-Object { [string]$_.finding_id -eq [string]$rw.finding_id }) | Select-Object -First 1
                        $requested = $f -and (@($f.history | Where-Object { $_.kind -eq 'opening_requested' }).Count -ge 1)
                        Case 2 $(if ($f -and $f.status -eq 'open' -and $requested) { 'pass' } else { 'fail' }) ("finding status " + $f.status + ", opening_requested=" + $requested)
                    }
                }
            }

            # ---- floor ----------------------------------------------------------------------
            if (-not $pipeF) { foreach ($i in 3..4) { Case $i 'not_covered' "no own floor and vertical pipe could be staged in '$doc'" } }
            else {
                $rf = RowFor $pipeF $floor
                $okF = $rf -and $rf.status -eq 'proposed' -and $rf.host_kind -eq 'floor' -and $rf.route -eq 'floor_opening' -and
                       $rf.shape -eq 'round' -and [double]$rf.opening_width_mm -ge 150
                if (-not $okF) { Case 3 'fail' ('no round floor_opening proposal for pipe ' + $pipeF + ': ' + (Json $rf)); Case 4 'unverified' 'no floor proposal to apply' }
                else {
                    Case 3 'pass' ("finding $($rf.finding_id): round $($rf.opening_width_mm) mm at " + (@($rf.crossing_point_mm) -join ','))
                    $af = ApplyCut $rf 'apply-floor'
                    Case 4 $(if ($af.ok) { 'pass' } else { 'fail' }) $af.detail
                }
            }

            # ---- beam: the cut is refused by name, and nothing is sent to apply ----------------
            if (-not $pipeB) { Case 5 'not_covered' "no own beam (OST_StructuralFraming type) and pipe could be staged in '$doc'" }
            else {
                $rb = RowFor $pipeB $beam
                $namedInPropose = $rb -and $rb.host_kind -eq 'framing_or_column' -and $rb.route -eq 'sleeve_only' -and $rb.cut_refused.code -eq 'member_cut_not_offered'
                $act = if ($rb) { ActionFor ([string]$rb.finding_id) } else { $null }
                $namedInApply = $false; $applyText = 'no action to rehearse'
                if ($act) {
                    # A rehearsal only (dry_run=true, no token): it must name the refusal and issue no token.
                    $reh = & $Ctx.Call 'horizun_resolve_clash' @{ operation = 'apply_opening'; target_document = $doc; clearance_mm = 50; allow_structural = $true; proposals = @($act); dry_run = $true }
                    $errs = if ($reh -and $reh.data) { @($reh.data.errors) } else { @() }
                    $namedInApply = (@($errs | Where-Object { $_.code -eq 'member_cut_not_offered' }).Count -ge 1) -and -not ($reh.data.confirmation_token)
                    $applyText = if ($reh) { (Json $errs) } else { 'no reply' }
                }
                Case 5 $(if ($namedInPropose -and $namedInApply) { 'pass' } else { 'fail' }) ("propose: " + (Json $rb.cut_refused) + " | apply rehearsal: " + $applyText)
            }
        }
        finally {
            $ids = @($created | Select-Object -Unique)
            if ($ids.Count -eq 0) { Case 6 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)   # newest first (openings before their hosts), the level last
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 50 } ($run + '-so-delete')
                # The probe's own findings would otherwise stay open, pointing at deleted elements.
                $fids = @(@($rows) | Where-Object { $_ -and $_.finding_id } | ForEach-Object { [string]$_.finding_id } | Select-Object -Unique)
                $notClosed = @()
                foreach ($fid in $fids) {
                    # update applies without a token (a dry run writes nothing and issues none): one call.
                    $c = & $Ctx.Call 'horizun_coordination' @{ operation = 'update'; finding_id = $fid; status = 'closed_by_decision'; note = 'live probe fixture, deleted' }
                    if ($c.isError -or -not $c.data -or $c.data.verified_after_reread -ne $true) { $notClosed += $fid }
                }
                $deleted = $del.stage -eq 'apply' -and -not $del.answer.isError
                if ($deleted -and $notClosed.Count -eq 0) { Case 6 'pass' ("deleted " + ($ids -join ',') + "; closed findings " + ($fids -join ',')) }
                elseif (-not $deleted) { Case 6 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
                else { Case 6 'fail' ('deleted, but these findings could not be closed: ' + ($notClosed -join ',')) }
            }
        }
        return $cases
    }
}
