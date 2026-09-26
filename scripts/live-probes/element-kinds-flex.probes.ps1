# Live probes for the newest horizun_create_elements kinds (sprinkler, flex_pipe,
# flex_duct, space, area, area_boundary) and horizun_mep_routing's resize now covering
# flex runs. Everything stands on the module's own level, own AreaScheme-backed area
# plan view (created if none exists) and whatever sprinkler/flex-pipe/flex-duct types
# the fixture happens to carry - discovered by querying it, never assumed by id or
# name. What was created is deleted at the end; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'element-kinds-flex'
    Catalog = @(
        @{ Name = 'sprinkler: place at a point and re-read position, type and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'flex_pipe: create a path and re-read its points, diameter and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'flex_duct: create a path and re-read its points, size and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'space: place at a point on a level and re-read the point and level'; Tool = 'horizun_create_elements' }
        @{ Name = 'area_boundary + area: close a loop on an area plan view and re-read the area'; Tool = 'horizun_create_elements' }
        @{ Name = 'mep_routing resize: a flex run moves to another catalog size and back, re-read both times'; Tool = 'horizun_mep_routing' }
        @{ Name = 'element-kinds-flex probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'sprinkler: place at a point and re-read position, type and level',
            'flex_pipe: create a path and re-read its points, diameter and level',
            'flex_duct: create a path and re-read its points, size and level',
            'space: place at a point on a level and re-read the point and level',
            'area_boundary + area: close a loop on an area plan view and re-read the area',
            'mep_routing resize: a flex run moves to another catalog size and back, re-read both times',
            'element-kinds-flex probes: everything created is deleted')
        $tools = @('horizun_create_elements', 'horizun_create_elements', 'horizun_create_elements', 'horizun_create_elements',
                   'horizun_create_elements', 'horizun_mep_routing', 'horizun_delete_verified')
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Verified($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data -and $r.answer.data.postconditions.all_verified -eq $true }
        function Why($r) { if ($r.stage -ne 'apply') { 'the rehearsal issued no token: ' + (Short $r.answer) } else { Short $r.answer } }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Instances($category, $max) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $false; include_links = $false; max_rows = $max }
            if (-not $q.data) { return @() }
            return @($q.data.rows)
        }
        # Create() wraps horizun_create_elements for ONE plan row and returns the FIRST
        # created element id, or $null; every id the row created (element_ids, when a
        # single row creates several - area_boundary's own curves) is tracked for cleanup.
        function Create($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-ekf-' + $key)
            if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                $ids = if ($row.element_ids) { @($row.element_ids | ForEach-Object { [long]$_ }) } elseif ($row.element_id) { @([long]$row.element_id) } else { @() }
                foreach ($id in $ids) { [void]$created.Add($id) }
                return @{ Row = $row; Reply = $r; Id = if ($ids.Count -gt 0) { $ids[0] } else { $null } }
            }
            return @{ Row = $null; Reply = $r; Id = $null }
        }

        # ---- staging: an own level far above everything, well clear of the real model. ----
        $E = 95000.0; $X = 700000.0; $Y = 0.0
        $lvOut = Create @(@{ kind = 'level'; name = "HZ_EKF_$run"; elevation = $E }) 'level'
        $levelId = $lvOut.Id

        # ==== 1: sprinkler ==================================================================
        $sprinklerType = Types 'OST_Sprinklers' | Select-Object -First 1
        if (-not $levelId -or -not $sprinklerType) {
            Case $catalog[0] $tools[0] 'not_covered' "no own level ($levelId) or no sprinkler type in the fixture"
        }
        else {
            $point = @($X, $Y, $E)
            $sp = Create @(@{ kind = 'sprinkler'; point = $point; type_id = $sprinklerType.element_id; level_id = $levelId; coordinate_mode = 'absolute' }) 'sprinkler'
            if (-not (Verified $sp.Reply)) {
                # A sprinkler family that MUST be hosted (work-plane based) refuses a level-only
                # placement by name; retry on a ceiling if the fixture offers one, else not_covered.
                $ceiling = Instances 'OST_Ceilings' 5 | Select-Object -First 1
                if ($ceiling) {
                    $sp = Create @(@{ kind = 'sprinkler'; point = $point; type_id = $sprinklerType.element_id; level_id = $levelId; coordinate_mode = 'absolute'; host_id = [long]$ceiling.element_id }) 'sprinkler-hosted'
                }
                if (-not (Verified $sp.Reply)) { Case $catalog[0] $tools[0] 'fail' (Why $sp.Reply) }
                else { Case $catalog[0] $tools[0] 'pass' ("sprinkler $($sp.Id) hosted on ceiling $($ceiling.element_id), position/type/level re-read") }
            }
            else { Case $catalog[0] $tools[0] 'pass' ("sprinkler $($sp.Id) at $($point -join ','), position/type/level re-read") }
        }

        # ==== 2/3: flex_pipe / flex_duct ====================================================
        $flexPipeType = Types 'OST_PipeCurves' | Where-Object { $_.family -match 'Flex' -or $_.type -match 'Flex' } | Select-Object -First 1
        $pipingSystem = Types 'OST_PipingSystem' | Select-Object -First 1
        if (-not $levelId -or -not $flexPipeType -or -not $pipingSystem) {
            Case $catalog[1] $tools[1] 'not_covered' "no own level, no flex pipe type or no piping system type in the fixture"
        }
        else {
            $points = @(@($X, ($Y + 2000), $E), @(($X + 1500), ($Y + 2200), $E), @(($X + 3000), ($Y + 2000), $E))
            $fp = Create @(@{ kind = 'flex_pipe'; points = $points; type_id = $flexPipeType.element_id; level_id = $levelId; system_type_id = $pipingSystem.element_id; diameter = 50 }) 'flex-pipe'
            if (Verified $fp.Reply) { Case $catalog[1] $tools[1] 'pass' ("flex_pipe $($fp.Id): 3 points, 50 mm, re-read") }
            else { Case $catalog[1] $tools[1] 'fail' (Why $fp.Reply) }
        }

        $flexDuctType = Types 'OST_DuctCurves' | Where-Object { $_.family -match 'Flex' -or $_.type -match 'Flex' } | Select-Object -First 1
        $ductSystem = Types 'OST_DuctSystem' | Select-Object -First 1
        if (-not $levelId -or -not $flexDuctType -or -not $ductSystem) {
            Case $catalog[2] $tools[2] 'not_covered' "no own level, no flex duct type or no duct system type in the fixture"
        }
        else {
            $points = @(@($X, ($Y + 4000), $E), @(($X + 1500), ($Y + 4200), $E), @(($X + 3000), ($Y + 4000), $E))
            $fd = Create @(@{ kind = 'flex_duct'; points = $points; type_id = $flexDuctType.element_id; level_id = $levelId; system_type_id = $ductSystem.element_id; diameter = 150 }) 'flex-duct'
            if (-not (Verified $fd.Reply)) {
                # This flex duct type may be rectangular, in which case diameter is the wrong
                # section for it - Revit's own "no settable parameter" refusal names that, and
                # width/height is the retry (plan-time cannot tell the shape; see Contract.cs).
                $fd = Create @(@{ kind = 'flex_duct'; points = $points; type_id = $flexDuctType.element_id; level_id = $levelId; system_type_id = $ductSystem.element_id; width = 200; height = 150 }) 'flex-duct-rect'
            }
            if (Verified $fd.Reply) { Case $catalog[2] $tools[2] 'pass' ("flex_duct $($fd.Id): 3 points, re-read") }
            else { Case $catalog[2] $tools[2] 'fail' (Why $fd.Reply) }
        }

        # ==== 4: space =======================================================================
        if (-not $levelId) { Case $catalog[3] $tools[3] 'not_covered' 'no own level to place a space on' }
        else {
            $sPoint = @(($X + 10000), $Y)
            $sp2 = Create @(@{ kind = 'space'; point = $sPoint; level_id = $levelId }) 'space'
            if (Verified $sp2.Reply) {
                $row = $sp2.Row
                Case $catalog[3] $tools[3] 'pass' ("space $($sp2.Id): level re-read; area_sqft=$($row.area_sqft), area_enclosed=$($row.area_enclosed)")
            }
            else { Case $catalog[3] $tools[3] 'fail' (Why $sp2.Reply) }
        }

        # ==== 5: area_boundary + area ========================================================
        $scheme = Instances 'OST_AreaSchemes' 20 | Select-Object -First 1
        $areaViewId = $null
        if (-not $levelId -or -not $scheme) {
            Case $catalog[4] $tools[4] 'not_covered' "no own level or no AreaScheme in the fixture"
        }
        else {
            $av = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; units = 'mm'
                    actions = @(@{ operation = 'create_area_plan'; level_id = $levelId; area_scheme_id = [long]$scheme.element_id; key = 'ekf-areaview' }) } ($run + '-ekf-areaview')
            $avRow = if ($av.stage -eq 'apply' -and -not $av.answer.isError -and $av.answer.data) { @($av.answer.data.rows) | Select-Object -First 1 }
            if ($av.stage -ne 'apply' -or $av.answer.isError -or -not $avRow -or -not $avRow.element_id) {
                Case $catalog[4] $tools[4] 'fail' ("create_area_plan: " + (Why $av))
            }
            else {
                $areaViewId = [long]$avRow.element_id; [void]$created.Add($areaViewId)
                $ax = $X + 20000; $ay = $Y
                $loop = @(@($ax, $ay, $E), @(($ax + 4000), $ay, $E), @(($ax + 4000), ($ay + 4000), $E), @($ax, ($ay + 4000), $E), @($ax, $ay, $E))
                $segments = for ($i = 0; $i -lt ($loop.Count - 1); $i++) { ,@($loop[$i], $loop[$i + 1]) }
                $ab = Create @(@{ kind = 'area_boundary'; profile = @(,$segments); view_id = $areaViewId }) 'area-boundary'
                if (-not (Verified $ab.Reply)) { Case $catalog[4] $tools[4] 'fail' ('area_boundary: ' + (Why $ab.Reply)) }
                else {
                    $ar = Create @(@{ kind = 'area'; point = @(($ax + 2000), ($ay + 2000)); view_id = $areaViewId }) 'area'
                    if (-not (Verified $ar.Reply)) { Case $catalog[4] $tools[4] 'fail' ('area: ' + (Why $ar.Reply)) }
                    else {
                        $row = $ar.Row
                        Case $catalog[4] $tools[4] 'pass' ("area $($ar.Id) inside a 4 m loop: area_sqft=$($row.area_sqft), area_enclosed=$($row.area_enclosed)")
                    }
                }
            }
        }

        # ==== 6: mep_routing resize on a flex run ===========================================
        $flexId = if ($fd -and $fd.Id) { $fd.Id } elseif ($fp -and $fp.Id) { $fp.Id } else { $null }
        if (-not $flexId) { Case $catalog[5] $tools[5] 'not_covered' 'no flex_pipe or flex_duct was created to resize' }
        else {
            $isDuct = ($fd -and $fd.Id -eq $flexId)
            $rd = & $Ctx.Call 'horizun_mep_routing' @{ operation = 'read'; target_document = $doc; units = 'mm'; element_ids = @($flexId) }
            $before = @($rd.data.elements) | Select-Object -First 1
            if (-not $before) { Case $catalog[5] $tools[5] 'fail' ('read: ' + (Short $rd)) }
            else {
                $round = ($null -ne $before.size.diameter)
                $catalogList = if ($isDuct) { @($rd.data.duct_sizes.round | ForEach-Object { [double]$_.nominal }) } else { @() }
                if (-not $round -or $catalogList.Count -lt 2) {
                    # No catalog to pick a second size from without live reads this module cannot
                    # fake; a rectangular flex duct or a one-size catalog is not_covered, not a fail.
                    Case $catalog[5] $tools[5] 'not_covered' 'the resized run has no second round catalog size to move to and back'
                }
                else {
                    $current = [double]$before.size.diameter
                    $other = $catalogList | Where-Object { [math]::Abs($_ - $current) -gt 0.01 } | Select-Object -First 1
                    $there = & $Ctx.Apply 'horizun_mep_routing' @{ operation = 'resize'; target_document = $doc; units = 'mm'; element_ids = @($flexId); diameter = $other } ($run + '-ekf-resize-there')
                    $back = $null
                    if ($there.stage -eq 'apply' -and -not $there.answer.isError -and $there.answer.data.state -eq 'committed_verified' -and $there.answer.data.postconditions.all_verified -eq $true) {
                        $back = & $Ctx.Apply 'horizun_mep_routing' @{ operation = 'resize'; target_document = $doc; units = 'mm'; element_ids = @($flexId); diameter = $current } ($run + '-ekf-resize-back')
                    }
                    $thereOk = ($there.stage -eq 'apply' -and -not $there.answer.isError -and $there.answer.data.postconditions.all_verified -eq $true)
                    $backOk = ($back -and $back.stage -eq 'apply' -and -not $back.answer.isError -and $back.answer.data.postconditions.all_verified -eq $true)
                    if (-not $thereOk) { Case $catalog[5] $tools[5] 'fail' ('resize: ' + (Short $there.answer)) }
                    elseif (-not $backOk) { Case $catalog[5] $tools[5] 'fail' ('resize back: ' + (Short $back.answer)) }
                    else { Case $catalog[5] $tools[5] 'pass' ("flex run $flexId $current -> $other -> $current mm, fitting postconditions verified both times") }
                }
            }
        }

        # ---- cleanup: newest first, the area view and level last. -------------------------
        $ids = @($created | Select-Object -Unique)
        if ($ids.Count -eq 0) { Case $catalog[6] $tools[6] 'not_covered' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-ekf-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[6] $tools[6] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[6] $tools[6] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Why $del)) }
        }
        return $cases
    }
}
