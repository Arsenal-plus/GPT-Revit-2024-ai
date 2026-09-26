# Live probe module: horizun_verify_changes scope=session, the data-only bbox
# fallback, and the tag/text-note overlap check (see README.md).
#
# 1. Two writes in a row: write A creates two walls of the SAME type at the SAME
#    location (a duplicate - an error write A's own automatic spatial_check
#    already reports), write B creates one harmless wall far away (its own check
#    is clean). Calling horizun_verify_changes with the DEFAULT scope=last_write
#    right after write B must NOT see write A's still-unresolved duplicate
#    (last_write's subjects are only write B's ids, and B is far from A).
#    scope=session must see it: it unions both writes' ids, so write A's walls
#    become subjects again and the duplicate reappears in spatial_check.findings.
# 2. horizun_write_params_verified (a DataOnlyTools member) changes a fresh
#    wall's WALL_BASE_OFFSET. The wall's bbox was cached when horizun_create_elements
#    made it (a normal write); this data-only call must therefore land in the
#    "known mover" tier of DataOnlyGeometryRules and carry a spatial_check whose
#    scope names a moved element - never the old "skipped outright" behaviour.
# 3. horizun_annotate places two text notes at the exact same point in the same
#    view. Their view-coordinate bounding boxes overlap, so BOTH the automatic
#    pass on horizun_annotate's own reply AND an explicit
#    horizun_verify_changes(include_annotation=true) must report the overlap.
#
# Everything created (level, walls, text notes) is deleted at the end.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'spatial-session-tags'
    Catalog = @(
        @{ Name = 'session: scope=session catches an earlier write''s duplicate that scope=last_write alone misses'; Tool = 'horizun_verify_changes' }
        @{ Name = 'session: write_params_verified moving a wall''s base offset gets a spatial result, not skipped';   Tool = 'horizun_write_params_verified' }
        @{ Name = 'session: two text notes on top of each other are reported as an annotation overlap';              Tool = 'horizun_annotate' }
        @{ Name = 'session: everything the probe created is deleted';                                                Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-session-tags' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { foreach ($i in 0..3) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-st-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $ids = @($r.answer.data.rows | ForEach-Object { [long]$_.element_id })
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ ids = $ids; reply = $r }
            }
            return @{ ids = @(); reply = $r }
        }

        try {
            $E = 145000.0; $X = 720000.0; $Y = 300000.0
            $lv = Create @(@{ kind = 'level'; name = "HZ_SESSION_$run"; elevation = $E }) 'level'
            $levelId = if ($lv.ids.Count -gt 0) { $lv.ids[0] } else { $null }
            $basic = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
            $plan = $null
            $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
            if ($qv.data) { $plan = @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true }) | Select-Object -First 1 }
            $textType = @(Types 'OST_TextNotes') | Select-Object -First 1

            if (-not $levelId -or -not $basic) {
                $why = "the fixture lacks what the probe stages (level=$levelId, basic wall=" + $basic.element_id + ')'
                foreach ($i in 0..2) { Case $i 'not_covered' $why }
            }
            else {
                # ---- 1. write A (a duplicate) then write B (harmless, far away) ----
                $wa = Create @(
                    @{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }
                    @{ kind = 'wall'; start = @($X, $Y, $E); end = @(($X + 6000), $Y, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }
                ) 'dup'
                $wb = Create @(@{ kind = 'wall'; start = @(($X + 60000), ($Y + 60000), $E); end = @(($X + 66000), ($Y + 60000), $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) 'far'
                if ($wa.ids.Count -lt 2 -or $wb.ids.Count -lt 1) {
                    Case 0 'unverified' ('could not stage the two writes: A=' + (Short $wa.reply.answer) + ' | B=' + (Short $wb.reply.answer))
                }
                else {
                    $lastOnly = & $Ctx.Call 'horizun_verify_changes' @{ target_document = $doc; capture = $false }
                    $sessionScoped = & $Ctx.Call 'horizun_verify_changes' @{ target_document = $doc; capture = $false; scope = 'session' }
                    $lastHasDup = @($lastOnly.data.spatial_check.findings | Where-Object { $_.kind -eq 'duplicate' -and ([long]$_.a.id -in $wa.ids -or [long]$_.b.id -in $wa.ids) }).Count -gt 0
                    $sessionHasDup = @($sessionScoped.data.spatial_check.findings | Where-Object { $_.kind -eq 'duplicate' -and ([long]$_.a.id -in $wa.ids -or [long]$_.b.id -in $wa.ids) }).Count -gt 0
                    $sessionSaysSession = $sessionScoped.data.scope.source -eq 'session' -and [int]$sessionScoped.data.scope.writes_considered -ge 2
                    $ok = (-not $lastHasDup) -and $sessionHasDup -and $sessionSaysSession -and -not $lastOnly.isError -and -not $sessionScoped.isError
                    Case 0 $(if ($ok) { 'pass' } else { 'fail' }) ('last_write_dup=' + $lastHasDup + ' session_dup=' + $sessionHasDup +
                        ' session_source=' + $sessionScoped.data.scope.source + ' writes_considered=' + $sessionScoped.data.scope.writes_considered +
                        ' last=' + (Short $lastOnly) + ' session=' + (Short $sessionScoped))
                }

                # ---- 2. write_params_verified moving a wall's base offset ----
                $w3 = Create @(@{ kind = 'wall'; start = @($X, ($Y + 30000), $E); end = @(($X + 6000), ($Y + 30000), $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) 'offset'
                if ($w3.ids.Count -eq 0) { Case 1 'unverified' ('could not stage the wall to move: ' + (Short $w3.reply.answer)) }
                else {
                    $w3Id = [long]$w3.ids[0]
                    $mv = & $Ctx.Apply 'horizun_write_params_verified' @{ target_document = $doc
                        writes = @(@{ target_id = $w3Id; parameter = 'WALL_BASE_OFFSET'; value = -10.0 }) } ($run + '-st-baseoffset')
                    $sc = if ($mv.stage -eq 'apply' -and $mv.answer.data) { $mv.answer.data.spatial_check } else { $null }
                    $notSkipped = $null -ne $sc
                    $sawMove = $notSkipped -and ([string]$sc.scope) -match 'moved'
                    $ok = $mv.stage -eq 'apply' -and -not $mv.answer.isError -and $notSkipped -and $sawMove
                    Case 1 $(if ($ok) { 'pass' } else { 'fail' }) ('stage=' + $mv.stage + ' error=' + $mv.answer.isError + ' spatial_check_present=' + $notSkipped +
                        ' scope=' + [string]$sc.scope + ' ' + (Short $mv.answer))
                }

                # ---- 3. two text notes on top of each other ----
                if (-not $plan -or -not $textType) {
                    Case 2 'not_covered' ("the fixture lacks what this case needs (plan=" + $plan.view_id + ', text_type=' + $textType.element_id + ')')
                }
                else {
                    $viewId = [long]$plan.view_id
                    $px = $X; $py = $Y + 45000.0
                    $an = & $Ctx.Apply 'horizun_annotate' @{ target_document = $doc
                        actions = @(
                            @{ operation = 'text'; view_id = $viewId; point = @($px, $py); text = "HZ_OVL_A_$run"; text_type_id = [long]$textType.element_id }
                            @{ operation = 'text'; view_id = $viewId; point = @($px, $py); text = "HZ_OVL_B_$run"; text_type_id = [long]$textType.element_id }
                        ) } ($run + '-st-textoverlap')
                    if ($an.stage -ne 'apply' -or $an.answer.isError -or -not $an.answer.data) {
                        Case 2 'unverified' ('could not place the two text notes: ' + (Short $an.answer))
                    }
                    else {
                        $textIds = @($an.answer.data.rows | ForEach-Object { [long]$_.element_id })
                        foreach ($id in $textIds) { [void]$created.Add($id) }
                        $autoFindings = @($an.answer.data.annotation_check.findings)
                        $vc = & $Ctx.Call 'horizun_verify_changes' @{ target_document = $doc; capture = $false; include_annotation = $true; view_ids = @($viewId) }
                        $explicitFindings = @($vc.data.annotation_check.findings)
                        $ok = $textIds.Count -eq 2 -and $autoFindings.Count -ge 1 -and -not $vc.isError -and $explicitFindings.Count -ge 1
                        Case 2 $(if ($ok) { 'pass' } else { 'fail' }) ('text_ids=' + ($textIds -join ',') + ' auto_findings=' + $autoFindings.Count +
                            ' explicit_findings=' + $explicitFindings.Count + ' ' + (Short $vc))
                    }
                }
            }
        }
        finally {
            $ids = @($created.ToArray())
            if ($ids.Count -eq 0) { Case 3 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-st-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 3 'pass' ('deleted ' + $ids.Count + ' created ids') }
                else { Case 3 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
