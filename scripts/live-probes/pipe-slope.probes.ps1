# Live probes for horizun_mep_routing operation=slope: three pipes joined by two
# elbows (an L then a Z in plan, all flat), sloped at 2 percent holding the first
# pipe's open end as the high end. The apply's own postconditions re-read every
# slope and connector pair; this module ALSO re-reads the connectors independently
# through horizun_query_model include_mep, before and after, and compares counts.
# Staged far from the model on the fixture's first level with whatever pipe type
# and piping system it carries; everything created is deleted, nothing is saved.
# What only this run can measure: whether Revit's elbows follow the moved pipe
# ends (the result's 'reconnected' list says whether the explicit reconnect ran).
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'pipe-slope'
    Catalog = @(
        @{ Name = 'pipe-slope: three pipes and two elbows staged flat'; Tool = 'horizun_create_elements' }
        @{ Name = 'pipe-slope: slope 2 percent from the upstream end, every pipe re-reads 2 percent within 0.05 pp'; Tool = 'horizun_mep_routing' }
        @{ Name = 'pipe-slope: every connector still connected after the slope, re-read independently'; Tool = 'horizun_query_model' }
        @{ Name = 'pipe-slope probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        $names = @(
            @{ Name = 'pipe-slope: three pipes and two elbows staged flat'; Tool = 'horizun_create_elements' }
            @{ Name = 'pipe-slope: slope 2 percent from the upstream end, every pipe re-reads 2 percent within 0.05 pp'; Tool = 'horizun_mep_routing' }
            @{ Name = 'pipe-slope: every connector still connected after the slope, re-read independently'; Tool = 'horizun_query_model' }
            @{ Name = 'pipe-slope probes: everything created is deleted'; Tool = 'horizun_delete_verified' })
        function Case($i, $outcome, $detail) { [void]$cases.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        if ($Ctx.WriteGate) { for ($i = 0; $i -lt 4; $i++) { Case $i 'not_covered' 'the write tier is closed for this run' }; return $cases }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Ok($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Find-Type($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; max_rows = 500; include_links = $false }
            if (-not $q.data) { return $null }
            $t = @($q.data.rows | Where-Object { $_.is_element_type })
            if ($t.Count -gt 0) { return [long]$t[0].element_id } else { return $null }
        }
        # Connected-connector count per element id, read through query_model include_mep.
        function Connected($ids) {
            $q = & $Ctx.Call 'horizun_query_model' @{ element_ids = @($ids); include_mep = $true; include_links = $false; max_rows = 50 }
            $out = @{}
            if (-not $q.data) { return $out }
            foreach ($row in @($q.data.rows)) {
                $conns = if ($row.connectors) { @($row.connectors) } elseif ($row.mep -and $row.mep.connectors) { @($row.mep.connectors) } else { @() }
                $out[[long]$row.element_id] = @($conns | Where-Object { $_.is_connected -eq $true }).Count
            }
            return $out
        }

        $lv = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 5; include_links = $false }
        $level = if ($lv.data -and @($lv.data.rows).Count -gt 0) { [long]@($lv.data.rows)[0].element_id } else { $null }
        $pipeType = Find-Type 'OST_PipeCurves'; $system = Find-Type 'OST_PipingSystem'
        if (-not $level -or -not $pipeType -or -not $system) {
            for ($i = 0; $i -lt 4; $i++) { Case $i 'not_covered' "'$doc' has no level, pipe type or piping system to stage a run" }
            return $cases
        }

        # ---- 1: stage P1 (x) -> corner -> P2 (y) -> corner -> P3 (x), then two elbows. ----
        $x = 560000.0; $y = 0.0; $z = 3000.0
        $pts = @(@($x, $y, $z), @(($x + 3000), $y, $z), @(($x + 3000), ($y + 2000), $z), @(($x + 6000), ($y + 2000), $z))
        $pipes = @()
        for ($k = 0; $k -lt 3; $k++) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                elements = @(@{ kind = 'pipe'; start = $pts[$k]; end = $pts[$k + 1]; diameter = 100; level_id = $level; type_id = $pipeType; system_type_id = $system }) } ($run + '-ps-pipe' + $k)
            if (Ok $r) { $id = [long]@($r.answer.data.rows)[0].element_id; $pipes += $id; [void]$created.Add($id) }
        }
        $elbows = @()
        if ($pipes.Count -eq 3) {
            for ($k = 0; $k -lt 2; $k++) {
                $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'
                    elements = @(@{ kind = 'fitting'; fitting = 'elbow'; elements = @(@{ element_id = $pipes[$k] }, @{ element_id = $pipes[$k + 1] }) }) } ($run + '-ps-elbow' + $k)
                if (Ok $r) { $id = [long]@($r.answer.data.rows)[0].element_id; $elbows += $id; [void]$created.Add($id) }
            }
        }
        if ($pipes.Count -ne 3 -or $elbows.Count -ne 2) {
            Case 0 'fail' ("staged " + $pipes.Count + " of 3 pipes and " + $elbows.Count + " of 2 elbows")
            Case 1 'not_covered' 'the run was not staged'; Case 2 'not_covered' 'the run was not staged'
        }
        else {
            Case 0 'pass' ("pipes " + ($pipes -join ',') + ", elbows " + ($elbows -join ','))
            $before = Connected ($pipes + $elbows)

            # ---- 2: slope 2 percent, the first pipe's open end held as the high end. ----
            $sl = & $Ctx.Apply 'horizun_mep_routing' @{ operation = 'slope'; target_document = $doc; units = 'mm'
                element_ids = $pipes; slope_percent = 2.0; fixed_end = ([string]$pipes[0] + ':high') } ($run + '-ps-slope')
            $d = if (Ok $sl) { $sl.answer.data } else { $null }
            if (-not $d -or $d.state -ne 'committed_verified' -or $d.postconditions.all_verified -ne $true) {
                Case 1 'fail' ('slope: ' + (Short $sl.answer)); Case 2 'not_covered' 'the slope did not commit'
            }
            else {
                $rows = @($d.result.pipes)
                $off = @($rows | Where-Object { $_.slope_percent -isnot [string] -and [math]::Abs([double]$_.slope_percent - 2.0) -gt 0.05 })
                $held = @($rows | Where-Object { [long]$_.element_id -eq $pipes[0] }) | Select-Object -First 1
                $drop = if ($held) { [double]$held.start_elevation - [double]$held.end_elevation } else { 0 }
                $reco = @($d.result.reconnected).Count
                if ($rows.Count -ne 3 -or $off.Count -gt 0) { Case 1 'fail' ("slopes re-read: " + (($rows | ForEach-Object { "$($_.element_id)=$($_.slope_percent)" }) -join ', ')) }
                elseif ([math]::Abs($drop) -lt 1) { Case 1 'fail' ("pipe $($pipes[0]) did not drop along its length (start $($held.start_elevation), end $($held.end_elevation))") }
                else { Case 1 'pass' ("3 pipes at " + (($rows | ForEach-Object { $_.slope_percent }) -join '/') + " percent; explicit reconnects: $reco") }

                # ---- 3: independent connector re-read. ----
                $after = Connected ($pipes + $elbows)
                $lost = @(($pipes + $elbows) | Where-Object { -not $after.ContainsKey($_) -or -not $before.ContainsKey($_) -or $after[$_] -lt $before[$_] })
                if ($before.Count -eq 0) { Case 2 'not_covered' 'query_model include_mep returned no connector rows' }
                elseif ($lost.Count -gt 0) { Case 2 'fail' ("fewer connected connectors after the slope on " + ($lost -join ',')) }
                else { Case 2 'pass' ("connected counts unchanged on " + ($pipes + $elbows).Count + " elements") }
            }
        }

        # ---- 4: cleanup, elbows first. ----
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case 3 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 50 } ($run + '-ps-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case 3 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case 3 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
