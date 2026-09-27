# Live probes: horizun_code_check operation=energy_readiness (Revit's energy model, built in a
# transaction that is always rolled back). Stages its OWN level far from the model, one room
# placed with NO walls (it must come back BY ID as not enclosed) and, 12 m away, a 6 x 4 m room
# closed by four walls with one window, so the energy model has an own space and an own window
# to count against a baseline read taken before staging. Wall and window types come BY NAME from
# this Revit's own Autodesk template. Everything created is deleted; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'energy-readiness'
    Catalog = @(
        @{ Name = 'energy: an own room placed without walls is listed by id in spaces.not_enclosed'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: the energy model is built and rolled back (built, rolled_back, azimuth_basis named)'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: the own walled room is enclosed and not missing from the energy model'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: surfaces without a construction are a count from 2024 and named not measurable in 2023'; Tool = 'horizun_code_check' }
        @{ Name = 'energy: window_to_wall has the four orientations N,E,S,W and counts the own window'; Tool = 'horizun_code_check' }
        @{ Name = 'energy probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $entries = @(@($script:HzProbeModules | Where-Object { $_.Name -eq 'energy-readiness' } | Select-Object -First 1).Catalog)
        $catalog = @($entries | ForEach-Object { $_.Name }); $tools = @($entries | ForEach-Object { $_.Tool })
        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $catalog.Count; $i++) { Case $catalog[$i] $tools[$i] 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $created = New-Object System.Collections.ArrayList
        $why = New-Object System.Collections.ArrayList
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Create($element, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($element) } ($run + '-enr-' + $key)
            if (Applied $r) {
                $row = @($r.answer.data.rows) | Select-Object -First 1
                if ($row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
            }
            [void]$why.Add("$key not created: stage=" + $r.stage + ' ' + (Short $r.answer))
            return $null
        }
        function TypeRows($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            return @($q.data.rows | Where-Object { $_.is_element_type })
        }
        function Named($rows, $want) {
            @($rows | Where-Object { $want -eq [string]$_.type -or $want -eq ([string]$_.family + ': ' + [string]$_.type) -or
                                     ([string]$_.type -and $want.EndsWith(': ' + [string]$_.type)) }) | Select-Object -First 1
        }
        # TemplateRoot exists only for the offline tests; a live run always uses Revit's own.
        $tplRoot = if ($Ctx.TemplateRoot) { [string]$Ctx.TemplateRoot } else { 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates' }
        $tpl = @('English\DefaultMetric.rte', 'Default_M_ENU.rte', 'English\Default-Multi-Discipline_Metric.rte') |
            ForEach-Object { Join-Path $tplRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        # A type BY NAME from the template, never "the first type" of the fixture: the refusal for a
        # name that cannot exist lists what the template holds, and the first listed name matching
        # $pattern is copied (or reused when the document already has it, then not deleted).
        function TemplateType($category, $pattern, $key) {
            if (-not $tpl) { [void]$why.Add("no Autodesk template under $tplRoot for $category"); return $null }
            $probe = & $Ctx.Call 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = $category; type_names = @('__hz_probe_no_such_type__') }
            $listed = [regex]::Match([string]$probe.text, 'Types there[^:]*:\s*(.+)$', 'Singleline')
            $names = if ($listed.Success) { @(($listed.Groups[1].Value -split ' \| ') | ForEach-Object { ($_ -replace '\s*(\.\.\.)?\.?\s*$', '').Trim() } | Where-Object { $_ }) } else { @() }
            $want = @($names | Where-Object { $_ -match $pattern }) | Select-Object -First 1
            if (-not $want) { [void]$why.Add("the template lists no $category type matching '$pattern': " + (Short $probe)); return $null }
            $have = Named (TypeRows $category) $want
            if ($have) { return $have }
            $cp = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl; category = $category; type_names = @($want); duplicate_types = 'use_destination' } ($run + '-enr-' + $key)
            $got = Named (TypeRows $category) $want
            if ($got) { [void]$created.Add([long]$got.element_id) } else { [void]$why.Add("copying '$want' gave no $category type: stage=" + $cp.stage + ' ' + (Short $cp.answer)) }
            return $got
        }
        function Energy { & $Ctx.Call 'horizun_code_check' @{ target_document = $doc; operation = 'energy_readiness'; max_findings = 5000 } }
        function WindowsOf($r) { if ($r.data -and $r.data.window_to_wall -and $r.data.window_to_wall.total) { [int]$r.data.window_to_wall.total.windows } else { 0 } }

        # Baseline BEFORE staging: the own window must raise the count, whatever the fixture holds.
        $before = Energy

        # ---- staging (mm): own level, a loose room, and a walled room with one window ----------
        $E = 104000.0; $X = 1140000.0; $Y = 0.0; $X2 = 1142000.0; $Y2 = 12000.0; $Wd = 6000.0; $Dp = 4000.0
        $levelId = Create @{ kind = 'level'; name = "HZ_ENR_$run"; elevation = $E } 'level'
        # A room takes an XY point (MEASURED 2026-09-26 in egress-travel: the XYZ form was refused).
        $looseRoom = if ($levelId) { Create @{ kind = 'room'; point = @($X, $Y); level_id = $levelId } 'loose-room' } else { $null }
        $wallType = TemplateType 'OST_Walls' 'Generic - 200' 'walltype'
        $windowType = TemplateType 'OST_Windows' 'Fixed' 'windowtype'
        $walls = @()
        if ($levelId -and $wallType) {
            $corners = @(@($X2, $Y2), @(($X2 + $Wd), $Y2), @(($X2 + $Wd), ($Y2 + $Dp)), @($X2, ($Y2 + $Dp)))
            for ($k = 0; $k -lt 4; $k++) {
                $a = $corners[$k]; $b = $corners[($k + 1) % 4]
                $walls += Create @{ kind = 'wall'; start = @($a[0], $a[1], $E); end = @($b[0], $b[1], $E); height = 3000
                                    level_id = $levelId; type_id = $wallType.element_id } "wall$k"
            }
        }
        $closed = @($walls | Where-Object { $_ }).Count -eq 4
        # A window is a family_instance hosted on its wall (create_elements has no 'window' kind).
        $windowId = if ($closed -and $windowType) {
            Create @{ kind = 'family_instance'; type_id = $windowType.element_id; point = @(($X2 + $Wd / 2), $Y2, ($E + 900)); coordinate_mode = 'absolute'
                      level_id = $levelId; host_id = $walls[0] } 'window'
        } else { $null }
        $roomId = if ($closed) { Create @{ kind = 'room'; point = @(($X2 + $Wd / 2), ($Y2 + $Dp / 2)); level_id = $levelId } 'room' } else { $null }
        $staging = "level=$levelId loose_room=$looseRoom walls=$(@($walls | Where-Object { $_ }).Count) window=$windowId room=$roomId " + ($why -join '; ')

        $m = Energy
        $rep = $m.data
        $em = if ($rep) { $rep.energy_model } else { $null }

        # ==== 1: the loose room comes back BY ID as not enclosed ===============================
        if (-not $looseRoom) { Case $catalog[0] $tools[0] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $listed1 = if ($rep -and $rep.spaces) { @($rep.spaces.not_enclosed | Where-Object { [long]$_.id -eq $looseRoom }).Count -gt 0 } else { $false }
            Case $catalog[0] $tools[0] $(if ($listed1) { 'pass' } else { 'fail' }) $(if ($rep) { "not_enclosed_count=$($rep.spaces.not_enclosed_count) own=$looseRoom listed=$listed1" } else { Short $m })
        }

        # ==== 2: built and ROLLED BACK - the only way this operation may touch the model =======
        if (-not $roomId) { Case $catalog[1] $tools[1] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $ok2 = -not $m.isError -and $em -and $em.built -eq $true -and $em.rolled_back -eq $true -and [string]$em.azimuth_basis -match '^(true_north|project_north|unverified)'
            Case $catalog[1] $tools[1] $(if ($ok2) { 'pass' } else { 'fail' }) $(if ($em) { "built=$($em.built) rolled_back=$($em.rolled_back) tier=$($em.tier) azimuth_basis=$($em.azimuth_basis) analytical_spaces=$($em.analytical_spaces)" } else { Short $m })
        }

        # ==== 3: the walled room is enclosed and the energy model has it =======================
        if (-not $roomId) { Case $catalog[2] $tools[2] 'unverified' ('staging incomplete: ' + $staging) }
        elseif (-not $rep -or -not $rep.spaces) { Case $catalog[2] $tools[2] 'fail' (Short $m) }
        else {
            $missing = $rep.spaces.not_in_energy_model
            $asEnclosed = @($rep.spaces.not_enclosed | Where-Object { [long]$_.id -eq $roomId }).Count -gt 0
            if ($missing -is [string]) { Case $catalog[2] $tools[2] 'unverified' ("the match is unavailable: $missing") }
            else {
                $isMissing = @($missing | Where-Object { $_ -and [long]$_.id -eq $roomId }).Count -gt 0
                Case $catalog[2] $tools[2] $(if (-not $asEnclosed -and -not $isMissing -and -not $m.isError) { 'pass' } else { 'fail' }) "own room $roomId in not_enclosed=$asEnclosed in not_in_energy_model=$isMissing"
            }
        }

        # ==== 4: constructions - a count from 2024, named not measurable in 2023 ================
        if (-not $roomId) { Case $catalog[3] $tools[3] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $surf = if ($rep) { $rep.surfaces } else { $null }
            # The own walls have types, so on a tier-Final model some surfaces MUST carry a construction:
            # a count equal to every surface is the below-Final symptom, not a measurement.
            $v4 = if (-not $surf) { 'fail' }
                  elseif ([int]$Ctx.Year -le 2023) { if ([string]$surf.without_construction -match 'not measurable') { 'pass' } else { 'fail' } }
                  elseif (([string]$surf.without_construction_count) -match '^\d+$') { if ([int]$surf.without_construction_count -lt [int]$surf.analytical_surfaces) { 'pass' } else { 'fail' } }
                  elseif ([int]$Ctx.Year -ge 2027 -and [string]$surf.without_construction -match 'not measurable') { 'unverified' }
                  else { 'fail' }
            Case $catalog[3] $tools[3] $v4 $(if ($surf) { "year=$($Ctx.Year) tier=$(if ($em) { $em.tier }) analytical_surfaces=$($surf.analytical_surfaces) without_construction_count=$($surf.without_construction_count) text=$(if ($surf.without_construction -is [string]) { $surf.without_construction })" } else { Short $m })
        }

        # ==== 5: WWR per orientation, and the own window raises the window count ================
        if (-not $windowId -or -not $roomId) { Case $catalog[4] $tools[4] 'unverified' ('staging incomplete: ' + $staging) }
        else {
            $wwr = if ($rep) { $rep.window_to_wall } else { $null }
            $rows = if ($wwr) { @($wwr.by_orientation) } else { @() }
            $order = (@($rows | ForEach-Object { $_.orientation }) -join ',')
            $gain = (WindowsOf $m) - (WindowsOf $before)
            Case $catalog[4] $tools[4] $(if ($order -eq 'N,E,S,W' -and $gain -ge 1) { 'pass' } else { 'fail' }) "orientations=$order windows_before=$(WindowsOf $before) windows_after=$(WindowsOf $m) unmeasured_wall_surfaces=$(if ($wwr) { $wwr.unmeasured_wall_surfaces })"
        }

        # ==== 6: cleanup, newest first (room, window, walls, types, loose room, level) ==========
        $ids = @($created | ForEach-Object { [long]$_ })
        if ($ids.Count -eq 0) { Case $catalog[5] $tools[5] 'unverified' 'nothing was created' }
        else {
            [array]::Reverse($ids)
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-enr-cleanup')
            if ($del.stage -eq 'apply' -and -not $del.answer.isError) { Case $catalog[5] $tools[5] 'pass' ("deleted " + $ids.Count + " created ids") }
            else { Case $catalog[5] $tools[5] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        return $cases
    }
}
