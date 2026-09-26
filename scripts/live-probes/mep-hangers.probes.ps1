# Live probes for horizun_mep_routing's "hangers" operation. Everything stands on the
# module's own two levels, far from the real model: an own floor on the upper level, an
# own pipe under it, and a second own pipe with nothing above it. The hanger type is a
# generic model the fixture carries (the Autodesk template ships some) - discovered by
# querying, never assumed by id or name; without one the cases are not_covered, with the
# reason. What was created is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'mep-hangers'
    Catalog = @(
        @{ Name = 'hangers: stations under an own floor, count, spacing and rod re-read'; Tool = 'horizun_mep_routing' }
        @{ Name = 'hangers: a run with nothing above is refused as no support, nothing placed'; Tool = 'horizun_mep_routing' }
        @{ Name = 'mep-hangers probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'hangers: stations under an own floor, count, spacing and rod re-read',
            'hangers: a run with nothing above is refused as no support, nothing placed',
            'mep-hangers probes: everything created is deleted')
        $tools = @('horizun_mep_routing', 'horizun_mep_routing', 'horizun_delete_verified')
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-hg-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            return $null
        }

        # ---- staging: two own levels, an own floor on the upper one, two own pipes. ----
        $E = 97000.0; $X = 820000.0; $Y = 0.0; $H = 3000.0; $Z = $E + 1000.0
        $pipeType = Types 'OST_PipeCurves' | Where-Object { $_.family -notmatch 'Flex' -and $_.type -notmatch 'Flex' } | Select-Object -First 1
        $system = Types 'OST_PipingSystem' | Select-Object -First 1
        $floorType = Types 'OST_Floors' | Select-Object -First 1
        $hangerTypes = @(Types 'OST_GenericModel' | Select-Object -First 3)
        $lvA = Create @(@{ kind = 'level'; name = "HZ_HG_A_$run"; elevation = $E }) 'level-a'
        $lvB = Create @(@{ kind = 'level'; name = "HZ_HG_B_$run"; elevation = ($E + $H) }) 'level-b'
        $floor = $null; $pipe = $null; $bare = $null
        if ($lvB -and $floorType) {
            $floor = Create @(@{ kind = 'floor'; level_id = $lvB; type_id = $floorType.element_id
                                 profile = @(,@(@(($X - 1000), ($Y - 2000), ($E + $H)), @(($X + 7000), ($Y - 2000), ($E + $H)), @(($X + 7000), ($Y + 2000), ($E + $H)), @(($X - 1000), ($Y + 2000), ($E + $H)))) }) 'floor'
        }
        if ($lvA -and $pipeType -and $system) {
            $pipe = Create @(@{ kind = 'pipe'; start = @($X, $Y, $Z); end = @(($X + 6000), $Y, $Z); diameter = 50; level_id = $lvA; type_id = [long]$pipeType.element_id; system_type_id = [long]$system.element_id }) 'pipe'
            $bare = Create @(@{ kind = 'pipe'; start = @(($X + 30000), $Y, $Z); end = @(($X + 36000), $Y, $Z); diameter = 50; level_id = $lvA; type_id = [long]$pipeType.element_id; system_type_id = [long]$system.element_id }) 'pipe-bare'
        }

        # ==== 1: hangers under the own floor =================================================
        # end_offset 300 and spacing 1500 on a 6000 mm run: stations at 300, 1650, 3000,
        # 4350 and 5700 (four equal gaps of 1350, none above 1500).
        $placedIds = @()
        if (-not $floor -or -not $pipe) { Case $catalog[0] $tools[0] 'not_covered' "no own floor ($floor) or pipe ($pipe) could be staged" }
        elseif ($hangerTypes.Count -eq 0) { Case $catalog[0] $tools[0] 'not_covered' 'the fixture carries no generic model type to use as a hanger' }
        else {
            $last = $null; $ok = $null
            foreach ($t in $hangerTypes) {
                $r = & $Ctx.Apply 'horizun_mep_routing' @{ operation = 'hangers'; target_document = $doc; element_ids = @($pipe); hanger_type_id = [long]$t.element_id
                        spacing_mm = 1500; end_offset_mm = 300; max_rod_mm = 5000 } ($run + '-hg-apply-' + $t.element_id)
                $last = $r
                if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data.postconditions.all_verified -eq $true) { $ok = $r; break }
                # A type Revit will not place free-standing is refused by name before any write; try the next.
                if ($r.stage -ne 'apply' -and (Short $r.answer) -match 'is placed|not a loaded family') { continue }
                break
            }
            if (-not $ok) {
                if ($last.stage -ne 'apply' -and (Short $last.answer) -match 'is placed') { Case $catalog[0] $tools[0] 'not_covered' ('no generic model type is level- or work-plane-based: ' + (Short $last.answer)) }
                else { Case $catalog[0] $tools[0] 'fail' ('hangers: ' + (Short $last.answer)) }
            }
            else {
                $placed = @($ok.answer.data.result.placed)
                $placedIds = @($placed | Where-Object { $_.element_id } | ForEach-Object { [long]$_.element_id })
                foreach ($id in $placedIds) { [void]$created.Add($id) }
                $along = @($placed | ForEach-Object { [double]$_.along_mm } | Sort-Object)
                $gaps = for ($i = 1; $i -lt $along.Count; $i++) { $along[$i] - $along[$i - 1] }
                $rods = @($placed | ForEach-Object { [double]$_.rod_mm })
                $onFloor = @($placed | Where-Object { $_.support.source -eq 'host' -and [long]$_.support.element_id -eq $floor }).Count
                $problems = @()
                if ($placed.Count -ne 5) { $problems += "placed $($placed.Count), expected 5" }
                if ($along.Count -gt 0 -and ([math]::Abs($along[0] - 300) -gt 1 -or [math]::Abs($along[-1] - 5700) -gt 1)) { $problems += "end stations at $($along[0]) / $($along[-1])" }
                if (@($gaps | Where-Object { $_ -gt 1501 }).Count -gt 0) { $problems += "a gap exceeds 1500: $($gaps -join ',')" }
                if ($onFloor -ne $placed.Count) { $problems += "$onFloor of $($placed.Count) hang from the own floor $floor" }
                if ($rods.Count -gt 0 -and (($rods | Measure-Object -Maximum).Maximum - ($rods | Measure-Object -Minimum).Minimum) -gt 1) { $problems += "rods differ under a flat floor: $($rods -join ',')" }
                if ($problems.Count -gt 0) { Case $catalog[0] $tools[0] 'fail' ($problems -join '; ') }
                else { Case $catalog[0] $tools[0] 'pass' ("5 hangers at $($along -join ',') mm, rod $($rods[0]) mm to floor $floor, positions/rotation re-read") }
            }
        }

        # ==== 2: nothing above -> refused, nothing placed ======================================
        if (-not $bare -or $hangerTypes.Count -eq 0) { Case $catalog[1] $tools[1] 'not_covered' 'no bare pipe or no generic model type to try' }
        else {
            $d = & $Ctx.Call 'horizun_mep_routing' @{ operation = 'hangers'; target_document = $doc; element_ids = @($bare); hanger_type_id = [long]$hangerTypes[0].element_id
                    spacing_mm = 1500; end_offset_mm = 300; max_rod_mm = 5000 }
            if ($d.isError -and (Short $d) -match 'no floor, framing or roof above') { Case $catalog[1] $tools[1] 'pass' ('refused before any write: ' + (Short $d)) }
            elseif ($d.isError -and (Short $d) -match 'is placed') { Case $catalog[1] $tools[1] 'not_covered' ('the generic model type is not placeable free-standing: ' + (Short $d)) }
            else { Case $catalog[1] $tools[1] 'fail' ('expected a no-support refusal, got: ' + $(if ($d.isError) { Short $d } else { 'a plan with ' + $d.data.plan.planned + ' stations' })) }
        }

        # ---- cleanup: hangers first, then runs, floor and levels. ----------------------------
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case $catalog[2] $tools[2] 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-hg-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[2] $tools[2] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[2] $tools[2] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
