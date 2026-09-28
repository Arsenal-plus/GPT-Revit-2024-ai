#Requires -Version 5.1
# Live probes for horizun_manage_styles, horizun_manage_units and horizun_electrical.
# Loaded by scripts/verify-live.ps1 (see README.md). Discovers what the disposable
# document holds; creates only its own subcategory / line pattern / panel schedule,
# deletes them again with horizun_delete_verified where Revit allows, and restores
# the length accuracy it changed. The document is never saved.

$script:HzProbeModules += [pscustomobject]@{
    Name    = 'styles-units-electrical'
    Catalog = @(
        @{ Name = 'styles: list object styles and line patterns read the document';                      Tool = 'horizun_manage_styles' }
        @{ Name = 'styles: create a subcategory with weight and colour, re-read it, then delete it';      Tool = 'horizun_manage_styles' }
        @{ Name = 'styles: create a line pattern, re-read its segments, then delete it';                  Tool = 'horizun_manage_styles' }
        @{ Name = 'units: read reports the length format';                                                Tool = 'horizun_manage_units' }
        @{ Name = 'units: change length accuracy, re-read it, restore the original and re-read again';   Tool = 'horizun_manage_units' }
        @{ Name = 'units: project_information reads every Project Information parameter';               Tool = 'horizun_manage_units' }
        @{ Name = 'electrical: list_panels and list_circuits read the document';                         Tool = 'horizun_electrical' }
        @{ Name = 'electrical: create a panel schedule for a panel without one and re-read its panel';   Tool = 'horizun_electrical' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Add-Case($name, $tool, $outcome, $detail) {
            [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = $detail })
        }
        function Short($answer) {
            $t = [string]$answer.text
            if ($t.Length -gt 300) { $t = $t.Substring(0, 300) + '...' }
            return $t
        }
        # A write is verified only when the APPLY answered committed_verified with an
        # all_verified checklist re-read from the committed model.
        function Test-Verified($applied) {
            return ($applied.stage -eq 'apply' -and -not $applied.answer.isError -and $null -ne $applied.answer.data -and
                    $applied.answer.data.state -eq 'committed_verified' -and
                    $applied.answer.data.postconditions.all_verified -eq $true)
        }
        # Delete what the probe created. Returns a sentence for the case detail.
        function Remove-Created($id, $label) {
            if ($null -eq $id) { return "$label id unknown; nothing to delete" }
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ mode = 'ids'; ids = @([long]$id); target_document = $Ctx.Document; id_cap = 5 } ("sue-del-$label")
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { return "$label $id deleted" }
            return "$label $id LEFT in the unsaved document (delete refused: " + (Short $del.answer) + ')'
        }

        $doc = $Ctx.Document
        $tag = 'HZ-probe-' + $Ctx.RunId
        $readArgs = @{}
        if (-not $Ctx.WriteGate -and $doc) { $readArgs['target_document'] = $doc }

        # ---------------------------------------------------------------- styles: reads
        $n = 'styles: list object styles and line patterns read the document'
        $a = & $Ctx.Call 'horizun_manage_styles' (@{ operation = 'list_object_styles' } + $readArgs)
        $b = & $Ctx.Call 'horizun_manage_styles' (@{ operation = 'list_line_patterns' } + $readArgs)
        if ($a.isError -or $b.isError) { Add-Case $n 'horizun_manage_styles' 'fail' ((Short $a) + ' | ' + (Short $b)) }
        elseif ($a.data.count -gt 0 -and $null -ne $b.data.solid_pattern_id) {
            Add-Case $n 'horizun_manage_styles' 'pass' ("{0} categories, {1} line patterns" -f $a.data.count, $b.data.count)
        }
        else { Add-Case $n 'horizun_manage_styles' 'fail' 'the listing came back empty: no categories or no solid pattern id' }

        # ---------------------------------------------------------------- units: reads
        $n = 'units: read reports the length format'
        $u = & $Ctx.Call 'horizun_manage_units' (@{ operation = 'read'; specs = @('length') } + $readArgs)
        $lengthRow = $null
        if (-not $u.isError -and $u.data) { $lengthRow = @($u.data.specs) | Where-Object { $_.spec -like '*:length-*' } | Select-Object -First 1 }
        if ($lengthRow -and $lengthRow.unit -and $null -ne $lengthRow.accuracy) {
            Add-Case $n 'horizun_manage_units' 'pass' ("{0} accuracy {1} symbol {2}" -f $lengthRow.unit, $lengthRow.accuracy, $lengthRow.symbol)
        } else { Add-Case $n 'horizun_manage_units' 'fail' (Short $u) }

        $n = 'units: project_information reads every Project Information parameter'
        $pi = & $Ctx.Call 'horizun_manage_units' (@{ operation = 'project_information' } + $readArgs)
        if (-not $pi.isError -and $pi.data.count -gt 0 -and $null -ne $pi.data.fields) {
            Add-Case $n 'horizun_manage_units' 'pass' ("{0} parameters; name='{1}'" -f $pi.data.count, $pi.data.fields.name)
        } else { Add-Case $n 'horizun_manage_units' 'fail' (Short $pi) }

        # ---------------------------------------------------------------- electrical: reads
        $n = 'electrical: list_panels and list_circuits read the document'
        $pl = & $Ctx.Call 'horizun_electrical' (@{ operation = 'list_panels' } + $readArgs)
        $cl = & $Ctx.Call 'horizun_electrical' (@{ operation = 'list_circuits' } + $readArgs)
        if ($pl.isError -or $cl.isError -or $null -eq $pl.data -or $null -eq $cl.data) {
            Add-Case $n 'horizun_electrical' 'fail' ((Short $pl) + ' | ' + (Short $cl))
        } else {
            Add-Case $n 'horizun_electrical' 'pass' ("{0} electrical equipment, {1} circuits (zero is an answer, not a failure)" -f $pl.data.count, $cl.data.count)
        }

        # ---------------------------------------------------------------- writes
        $writeNames = @(
            @{ N = 'styles: create a subcategory with weight and colour, re-read it, then delete it'; T = 'horizun_manage_styles' }
            @{ N = 'styles: create a line pattern, re-read its segments, then delete it'; T = 'horizun_manage_styles' }
            @{ N = 'units: change length accuracy, re-read it, restore the original and re-read again'; T = 'horizun_manage_units' }
            @{ N = 'electrical: create a panel schedule for a panel without one and re-read its panel'; T = 'horizun_electrical' }
        )
        if ($Ctx.WriteGate) {
            foreach ($w in $writeNames) { Add-Case $w.N $w.T 'not_covered' 'the write tier is gated for this run' }
            return $cases.ToArray()
        }

        # styles: subcategory
        $n = $writeNames[0].N
        $sub = & $Ctx.Apply 'horizun_manage_styles' @{ operation = 'create_subcategory'; target_document = $doc; category = 'OST_GenericModel'
                                                      name = $tag; projection_weight = 3; color = '#C03020' } 'sue-subcat'
        if (Test-Verified $sub) {
            $id = $sub.answer.data.result.id
            $check = & $Ctx.Call 'horizun_manage_styles' @{ operation = 'list_object_styles'; target_document = $doc; category = 'OST_GenericModel' }
            $row = @($check.data.category.subcategories) | Where-Object { $_.name -eq $tag } | Select-Object -First 1
            $listed = $row -and $row.projection_weight -eq 3 -and $row.color -eq '#C03020'
            $cleanup = Remove-Created $id 'subcategory'
            Add-Case $n 'horizun_manage_styles' $(if ($listed) { 'pass' } else { 'fail' }) ("created $id, listed=$listed; $cleanup")
        } else { Add-Case $n 'horizun_manage_styles' 'fail' ("stage $($sub.stage): " + (Short $sub.answer)) }

        # styles: line pattern
        $n = $writeNames[1].N
        $lp = & $Ctx.Apply 'horizun_manage_styles' @{ operation = 'create_line_pattern'; target_document = $doc; name = $tag
                                                     segments = @(@{ type = 'dash'; length = 6 }, @{ type = 'space'; length = 3 }, @{ type = 'dot' }, @{ type = 'space'; length = 3 }) } 'sue-linepattern'
        if (Test-Verified $lp) {
            $id = $lp.answer.data.result.line_pattern_id
            $check = & $Ctx.Call 'horizun_manage_styles' @{ operation = 'list_line_patterns'; target_document = $doc }
            $row = @($check.data.line_patterns) | Where-Object { $_.name -eq $tag } | Select-Object -First 1
            $listed = $row -and @($row.segments).Count -eq 4
            $cleanup = Remove-Created $id 'line_pattern'
            Add-Case $n 'horizun_manage_styles' $(if ($listed) { 'pass' } else { 'fail' }) ("created $id, 4 segments listed=$listed; $cleanup")
        } else { Add-Case $n 'horizun_manage_styles' 'fail' ("stage $($lp.stage): " + (Short $lp.answer)) }

        # units: accuracy round trip
        $n = $writeNames[2].N
        if (-not $lengthRow) { Add-Case $n 'horizun_manage_units' 'unverified' 'the length format could not be read, so there is no original to restore' }
        else {
            $original = [double]$lengthRow.accuracy
            $candidates = @(1.0, 0.1, 0.01, 10.0, 0.5) | Where-Object { $_ -ne $original }
            $changed = $null; $tried = @()
            foreach ($acc in $candidates) {
                $set = & $Ctx.Apply 'horizun_manage_units' @{ operation = 'set'; target_document = $doc; spec = 'length'; accuracy = $acc } ("sue-acc-$acc")
                if (Test-Verified $set) { $changed = $acc; break }
                $tried += "$acc -> " + (Short $set.answer)
            }
            if ($null -eq $changed) { Add-Case $n 'horizun_manage_units' 'fail' ('no accuracy could be applied: ' + ($tried -join ' | ')) }
            else {
                $back = & $Ctx.Apply 'horizun_manage_units' @{ operation = 'set'; target_document = $doc; spec = 'length'; accuracy = $original } 'sue-acc-restore'
                $re = & $Ctx.Call 'horizun_manage_units' @{ operation = 'read'; target_document = $doc; specs = @('length') }
                $now = (@($re.data.specs) | Select-Object -First 1).accuracy
                $restored = (Test-Verified $back) -and $null -ne $now -and [math]::Abs([double]$now - $original) -lt 1e-12
                Add-Case $n 'horizun_manage_units' $(if ($restored) { 'pass' } else { 'fail' }) ("accuracy $original -> $changed -> re-read $now (restore verified=$(Test-Verified $back))")
            }
        }

        # electrical: panel schedule
        $n = $writeNames[3].N
        $panels = @($pl.data.panels) | Where-Object { $_.is_panel -eq $true }
        # STAGE A PANEL when the write model has none (HZ_WRITE is an HVAC sample):
        # Autodesk's own electrical template of the run's year carries a face-hosted
        # panelboard; copy its type, host it on an own wall with the point 250 mm in
        # front of the wall's +Y face (a point inside the wall is refused as no side
        # could be chosen - measured 2026-09-26), and delete both afterwards.
        $staged = @()
        $stageNote = $null
        # Always an own panel, tried first: the 2023 release model's panels ALL carry a panel
        # schedule already (MEASURED 2026-09-27, v2.1.3 gate), so an existing panel proves nothing.
        if ($true) {
            $tpl = "C:\ProgramData\Autodesk\RVT $($Ctx.Year)\Templates\English\Electrical-Default_Metric.rte"
            $panelName = 'M_Lighting and Appliance Panelboard - 208V MLO: 100 A'
            if (-not (Test-Path -LiteralPath $tpl)) { $stageNote = "no electrical template at $tpl" }
            else {
                $cp = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); type_names = @($panelName); category = 'OST_ElectricalEquipment'; duplicate_types = 'use_destination' } 'sue-panel-type'
                $types = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_ElectricalEquipment'); include_types = $true; include_links = $false; max_rows = 500 }
                $ptype = @($types.data.rows | Where-Object { $_.is_element_type -and [string]$_.family -like 'M_Lighting and Appliance Panelboard - 208V MLO*' -and [string]$_.name -eq '100 A' }) | Select-Object -First 1
                $lvRows = & $Ctx.Call 'horizun_list_elements' @{ category = 'OST_Levels'; max_rows = 20; include_links = $false }
                $lv = @($lvRows.data.rows | Sort-Object { [double]$_.elevation }) | Select-Object -First 1
                if (-not $ptype -or -not $lv) { $stageNote = 'the panelboard type could not be copied from the template: ' + (Short $cp.answer) }
                else {
                    $wl = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'wall'; start = @(960000, 0, 0); end = @(964000, 0, 0); level_id = [long]$lv.element_id; height = 3000 }) } 'sue-panel-wall'
                    $wallId = if ($wl.stage -eq 'apply' -and -not $wl.answer.isError) { [long]@($wl.answer.data.rows)[0].element_id } else { $null }
                    if ($wallId) { $staged += $wallId }
                    $pn = if ($wallId) { & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'family_instance'; type_id = [long]$ptype.element_id; host_id = $wallId; point = @(962000, 250, 1200); coordinate_mode = 'absolute'; level_id = [long]$lv.element_id }) } 'sue-panel-place' } else { $null }
                    if ($pn -and $pn.stage -eq 'apply' -and -not $pn.answer.isError) { $staged = @([long]@($pn.answer.data.rows)[0].element_id) + $staged }
                    else { $stageNote = 'the panelboard could not be placed: ' + $(if ($pn) { Short $pn.answer } else { 'no host wall: ' + (Short $wl.answer) }) }
                    $pl = & $Ctx.Call 'horizun_electrical' (@{ operation = 'list_panels' } + $readArgs)
                    $panels = @($pl.data.panels) | Where-Object { $_.is_panel -eq $true }
                    $panels = @(@($panels | Where-Object { $staged -contains [long]$_.id }) + @($panels | Where-Object { $staged -notcontains [long]$_.id }))
                }
            }
        }
        if ($panels.Count -eq 0) {
            Add-Case $n 'horizun_electrical' 'not_covered' ('the disposable document holds no electrical equipment that is a panel and none could be staged: ' + $stageNote)
        } else {
            $done = $false; $notes = @()
            foreach ($p in $panels) {
                $ps = & $Ctx.Apply 'horizun_electrical' @{ operation = 'panel_schedule'; target_document = $doc; panel_id = [long]$p.id } ("sue-ps-$($p.id)")
                if (Test-Verified $ps) {
                    $cleanup = Remove-Created $ps.answer.data.result.panel_schedule_view_id 'panel_schedule_view'
                    Add-Case $n 'horizun_electrical' 'pass' ("panel $($p.id): view $($ps.answer.data.result.panel_schedule_view_id) re-reads its panel; $cleanup")
                    $done = $true; break
                }
                $notes += "panel $($p.id): " + (Short $ps.answer)
                if ($notes.Count -ge 3) { break }
            }
            if (-not $done) {
                $allHave = @($notes | Where-Object { $_ -notmatch 'already has panel schedule view' }).Count -eq 0
                Add-Case $n 'horizun_electrical' $(if ($allHave) { 'not_covered' } else { 'fail' }) ($notes -join ' | ')
            }
        }
        if ($staged.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($staged); id_cap = 10 } 'sue-panel-cleanup' }
        return $cases.ToArray()
    }
}
