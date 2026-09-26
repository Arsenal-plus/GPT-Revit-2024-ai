# Live probes for horizun_mep_routing operation=slope: three pipes joined by two
# elbows (an L then a Z in plan, all flat), sloped at 2 percent holding the first
# pipe's open end as the high end. The apply's own postconditions re-read every
# pipe end, slope and connector pair; this module ALSO checks, from the result,
# that the held end stayed at 3000 mm and every pipe falls toward the outlet, and
# re-reads the connectors independently through horizun_query_model include_mep
# (mep.connectors[].origin, mm): connected counts must not drop and every elbow
# connector must sit within 1 mm of a pipe end - IsConnected alone is the same
# signal the command's own postconditions use, the origins are not.
# Staged far from the model on the fixture's first level with whatever pipe type
# and piping system it carries; everything created is deleted, nothing is saved.
# What only this run can measure: whether Revit's elbows follow the moved pipe
# ends (origin coincidence) and whether the explicit reconnect ran ('reconnected').
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'pipe-slope'
    Catalog = @(
        @{ Name = 'pipe-slope: three pipes and two elbows staged flat'; Tool = 'horizun_create_elements' }
        @{ Name = 'pipe-slope: slope 2 percent held high at the first pipe, the held end stays at 3000 mm and every pipe falls toward the outlet at 2 percent within 0.05 pp'; Tool = 'horizun_mep_routing' }
        @{ Name = 'pipe-slope: every connector still connected and every elbow connector within 1 mm of a pipe end, re-read independently'; Tool = 'horizun_query_model' }
        @{ Name = 'pipe-slope probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        $names = @(
            @{ Name = 'pipe-slope: three pipes and two elbows staged flat'; Tool = 'horizun_create_elements' }
            @{ Name = 'pipe-slope: slope 2 percent held high at the first pipe, the held end stays at 3000 mm and every pipe falls toward the outlet at 2 percent within 0.05 pp'; Tool = 'horizun_mep_routing' }
            @{ Name = 'pipe-slope: every connector still connected and every elbow connector within 1 mm of a pipe end, re-read independently'; Tool = 'horizun_query_model' }
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
        # Connectors per element id, as query_model include_mep returns them (row.mep.connectors).
        function Read-Mep($ids) {
            $q = & $Ctx.Call 'horizun_query_model' @{ element_ids = @($ids); include_mep = $true; include_links = $false; max_rows = 50 }
            $out = @{}
            if (-not $q.data) { return $out }
            foreach ($row in @($q.data.rows)) { if ($row.mep) { $out[[long]$row.element_id] = @($row.mep.connectors) } }
            return $out
        }
        function Connected($mep) { $c = @{}; foreach ($k in $mep.Keys) { $c[$k] = @($mep[$k] | Where-Object { $_.is_connected -eq $true }).Count }; return $c }
        function Dist($a, $b) { $dx = [double]$a[0] - [double]$b[0]; $dy = [double]$a[1] - [double]$b[1]; $dz = [double]$a[2] - [double]$b[2]; [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz) }

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
            $before = Connected (Read-Mep ($pipes + $elbows))

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
                # Staged in flow order: every pipe's start is its upstream end, so start must sit HIGHER.
                $rising = @($rows | Where-Object { ([double]$_.start_elevation - [double]$_.end_elevation) -le 1 })
                $reco = @($d.result.reconnected).Count
                if ($rows.Count -ne 3 -or $off.Count -gt 0) { Case 1 'fail' ("slopes re-read: " + (($rows | ForEach-Object { "$($_.element_id)=$($_.slope_percent)" }) -join ', ')) }
                elseif (-not $held -or [math]::Abs([double]$held.start_elevation - $z) -gt 0.5) { Case 1 'fail' ("the held end of pipe $($pipes[0]) moved: start_elevation $($held.start_elevation), expected $z") }
                elseif ($rising.Count -gt 0) { Case 1 'fail' ("pipes not falling toward the outlet: " + (($rising | ForEach-Object { "$($_.element_id) $($_.start_elevation)->$($_.end_elevation)" }) -join ', ')) }
                else { Case 1 'pass' ("3 pipes at " + (($rows | ForEach-Object { $_.slope_percent }) -join '/') + " percent; explicit reconnects: $reco") }

                # ---- 3: independent connector re-read. ----
                $mep = Read-Mep ($pipes + $elbows); $after = Connected $mep
                $lost = @(($pipes + $elbows) | Where-Object { -not $after.ContainsKey($_) -or -not $before.ContainsKey($_) -or $after[$_] -lt $before[$_] })
                $pipeEnds = @(foreach ($p in $pipes) { if ($mep.ContainsKey($p)) { @($mep[$p] | Where-Object { $_.origin }) } })
                $apart = @(); $worst = 0.0
                foreach ($e in $elbows) {
                    foreach ($c in @($mep[$e] | Where-Object { $_.origin })) {
                        $near = ($pipeEnds | ForEach-Object { Dist $c.origin $_.origin } | Measure-Object -Minimum).Minimum
                        if ($null -eq $near) { $near = [double]::PositiveInfinity }
                        if ($near -gt $worst) { $worst = $near }
                        if ($near -gt 1.0) { $apart += "$e#$($c.id) $([math]::Round($near, 1)) mm" }
                    }
                }
                if ($before.Count -eq 0) { Case 2 'not_covered' 'query_model include_mep returned no connector rows' }
                elseif ($lost.Count -gt 0) { Case 2 'fail' ("fewer connected connectors after the slope on " + ($lost -join ',')) }
                elseif ($pipeEnds.Count -eq 0) { Case 2 'not_covered' 'query_model returned no connector origins' }
                elseif ($apart.Count -gt 0) { Case 2 'fail' ("elbow connectors off their pipe ends: " + ($apart -join ', ')) }
                else { Case 2 'pass' ("connected counts unchanged on " + ($pipes + $elbows).Count + " elements; worst elbow-to-pipe gap " + [math]::Round($worst, 3) + " mm") }
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
