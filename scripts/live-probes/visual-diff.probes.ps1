# Live probe module: horizun_verify_changes operation=snapshot / compare_to - the
# before/after visual diff (see README.md). Stages, on an OWN level far from the
# fixture, two anchor walls that fix the frame; snapshots that frame; adds a third
# wall INSIDE it; compare_to must report changed pixels, a diff image and a region
# attributed to the new wall. A second snapshot of the unchanged scene compared at
# once must come back ~0 (the stored camera reproduces the same pixels). No view may
# survive; the walls, the level and the probe's baseline files are all deleted.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'visual-diff'
    Catalog = @(
        @{ Name = 'visual-diff: snapshot of an own frame saves a baseline PNG and its camera';                 Tool = 'horizun_verify_changes' }
        @{ Name = 'visual-diff: compare_to after an own wall reports changed pixels and writes a diff image';  Tool = 'horizun_verify_changes' }
        @{ Name = 'visual-diff: compare_to attributes a changed region to the new wall and lists it as changed'; Tool = 'horizun_verify_changes' }
        @{ Name = 'visual-diff: compare_to against a fresh snapshot without changes reports a ratio of ~0';   Tool = 'horizun_verify_changes' }
        @{ Name = 'visual-diff: snapshot and compare_to leave no view behind';                                 Tool = 'horizun_verify_changes' }
        @{ Name = 'visual-diff: everything the probe created is deleted (walls, level, baseline files)';       Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'visual-diff' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        # $Ctx.WriteGate TRUE means the write tier is CLOSED (verify-live keeps the reason).
        if ($Ctx.WriteGate) { foreach ($i in 0..5) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        $files = New-Object System.Collections.ArrayList
        $rollbacks = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-vd-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $ids = @($r.answer.data.rows | ForEach-Object { [long]$_.element_id })
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ ids = $ids; reply = $r }
            }
            return @{ ids = @(); reply = $r }
        }
        function ViewCount {
            $v = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Views'; max_rows = 1; include_links = $false }
            if ($v.data -and $null -ne $v.data.total) { return [int]$v.data.total }
            if ($v.data -and $null -ne $v.data.count) { return [int]$v.data.count }
            return $null
        }
        function Verify($op, $name, $extra) {
            $a = @{ target_document = $doc; operation = $op; snapshot_name = $name }
            if ($extra) { foreach ($k in $extra.Keys) { $a[$k] = $extra[$k] } }
            $r = & $Ctx.Call 'horizun_verify_changes' $a
            if ($r.data) {
                [void]$rollbacks.Add([string]$r.data.temporary_view_rollback)
                foreach ($p in @($r.data.baseline_png, $r.data.baseline_camera)) { if ($p) { [void]$files.Add([string]$p) } }
            }
            return $r
        }

        try {
            $E = 140000.0; $X = 700000.0; $Y = 0.0
            $lv = Create @(@{ kind = 'level'; name = "HZ_VDIFF_$run"; elevation = $E }) 'level'
            $levelId = if ($lv.ids.Count -gt 0) { $lv.ids[0] } else { $null }
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Walls'); include_types = $true; include_links = $false; max_rows = 500 }
            $basic = @(@($q.data.rows) | Where-Object { $_.is_element_type -and -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }) | Select-Object -First 1
            if (-not $levelId -or -not $basic) {
                foreach ($i in 0..4) { Case $i 'not_covered' ("the fixture lacks what the probe stages (level=$levelId, basic wall type=" + $basic.element_id + ')') }
            }
            else {
                function Wall($x0, $y0, $x1, $y1, $key) { Create @(@{ kind = 'wall'; start = @($x0, $y0, $E); end = @($x1, $y1, $E); level_id = $levelId; type_id = $basic.element_id; height = 3000 }) $key }
                # Two anchors 4 m apart fix a frame whose section box holds the third wall.
                $a1 = Wall $X $Y ($X + 4000) $Y 'anchor-south'
                $a2 = Wall $X ($Y + 4000) ($X + 4000) ($Y + 4000) 'anchor-north'
                if ($a1.ids.Count -eq 0 -or $a2.ids.Count -eq 0) {
                    foreach ($i in 0..4) { Case $i 'unverified' ('the anchor walls could not be staged: ' + (Short $a1.reply.answer) + ' | ' + (Short $a2.reply.answer)) }
                }
                else {
                    $frame = @([long]$a1.ids[0], [long]$a2.ids[0])
                    $viewsBefore = ViewCount
                    $base = "hz_vd_$run"
                    # 0. snapshot
                    $s = Verify 'snapshot' $base @{ element_ids = $frame; pixel_size = 800 }
                    $okSnap = -not $s.isError -and $s.data.status -eq 'ok' -and $s.data.baseline_png -and (Test-Path -LiteralPath ([string]$s.data.baseline_png)) -and [int]$s.data.width -gt 0
                    Case 0 $(if ($okSnap) { 'pass' } else { 'fail' }) ('error=' + $s.isError + ' status=' + $s.data.status + ' png=' + $s.data.baseline_png + ' size=' + $s.data.width + 'x' + $s.data.height + ' ' + (Short $s))
                    if (-not $okSnap) { foreach ($i in 1..3) { Case $i 'unverified' 'no baseline to compare against' } }
                    else {
                        # 1 + 2. a new wall inside the frame
                        $w = Wall ($X + 1000) ($Y + 2000) ($X + 3000) ($Y + 2000) 'new-wall'
                        if ($w.ids.Count -eq 0) { foreach ($i in 1..2) { Case $i 'unverified' ('the new wall could not be placed: ' + (Short $w.reply.answer)) } }
                        else {
                            $wallId = [long]$w.ids[0]
                            $c = Verify 'compare_to' $base $null
                            $ratio = [double]$c.data.changed_pixel_ratio
                            $okDiff = -not $c.isError -and $ratio -gt 0 -and $c.data.diff_path -and (Test-Path -LiteralPath ([string]$c.data.diff_path))
                            Case 1 $(if ($okDiff) { 'pass' } else { 'fail' }) ('error=' + $c.isError + ' ratio=' + $ratio + ' regions=' + @($c.data.regions).Count + ' diff=' + $c.data.diff_path + ' ' + (Short $c))
                            $named = @($c.data.regions | Where-Object { @($_.element_ids | ForEach-Object { [long]$_ }) -contains $wallId })
                            $listed = @($c.data.elements_changed_since_baseline | ForEach-Object { [long]$_ }) -contains $wallId
                            Case 2 $(if ($named.Count -ge 1 -and $listed) { 'pass' } else { 'fail' }) ("wall=$wallId listed=$listed regions_naming_it=" + $named.Count + ' first_region=' + (@($c.data.regions)[0] | ConvertTo-Json -Compress -Depth 4))
                        }
                        # 3. a fresh snapshot of the unchanged scene, compared at once
                        $after = "hz_vd_${run}_after"
                        $s2 = Verify 'snapshot' $after @{ element_ids = $frame; pixel_size = 800 }
                        $c2 = if (-not $s2.isError) { Verify 'compare_to' $after $null } else { $null }
                        if (-not $c2) { Case 3 'unverified' ('the second snapshot failed: ' + (Short $s2)) }
                        else { $r2 = [double]$c2.data.changed_pixel_ratio; Case 3 $(if (-not $c2.isError -and $null -ne $c2.data.changed_pixel_ratio -and $r2 -le 0.001) { 'pass' } else { 'fail' }) ('error=' + $c2.isError + " ratio=$r2 regions=" + @($c2.data.regions).Count) }
                    }
                    # 4. nothing left behind
                    $viewsAfter = ViewCount
                    $bad = @($rollbacks | Where-Object { $_ -ne 'RolledBack' })
                    if ($null -eq $viewsBefore -or $null -eq $viewsAfter) { Case 4 'unverified' ("the view count could not be read (before=$viewsBefore, after=$viewsAfter); rollbacks=" + ($rollbacks -join ',')) }
                    else { Case 4 $(if ($viewsBefore -eq $viewsAfter -and $rollbacks.Count -gt 0 -and $bad.Count -eq 0) { 'pass' } else { 'fail' }) ("views before=$viewsBefore after=$viewsAfter rollbacks=" + ($rollbacks -join ',')) }
                }
            }
        }
        finally {
            $ids = @($created.ToArray())
            $leftFiles = @()
            foreach ($f in $files) { try { Remove-Item -LiteralPath $f -Force -ErrorAction Stop } catch { }; if (Test-Path -LiteralPath $f) { $leftFiles += $f } }
            if ($ids.Count -eq 0) { Case 5 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-vd-cleanup')
                if ($del.stage -eq 'apply' -and -not $del.answer.isError -and $leftFiles.Count -eq 0) { Case 5 'pass' ('deleted ' + $ids.Count + ' created ids and ' + $files.Count + ' baseline files') }
                else { Case 5 'fail' ('left behind: ids ' + ($ids -join ',') + ' files ' + ($leftFiles -join ',') + ' - ' + (Short $del.answer)) }
            }
        }
        return $out.ToArray()
    }
}
