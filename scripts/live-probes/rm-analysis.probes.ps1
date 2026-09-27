# Live probes for the read-only analysis operations: horizun_plan_mep operation=system_analysis
# and horizun_query_structure mode=analytical / mode=loads. Everything the reads judge is staged
# on the module's own level at X = 1,130,000 mm (branch C's slot), far from the model: an own
# pipe and an own duct - Revit puts each run in a system of its own type, found through the
# run's connectors (query_model include_mep) and read BY ID - and an own structural beam and
# column. Types come BY NAME, from the document or else copied from the year's Autodesk
# templates with horizun_copy_between_documents; a name found nowhere is not_covered with the
# copy's own refusal, never "the first type". No typed tool creates a PointLoad
# (horizun_create_elements has no load kind), so the own-load case is not_covered with that
# reason; the loads read is still exercised on whatever the document carries, and is
# not_covered when it carries no load (no row read, no kN conversion ran). Revit 2023+ does
# not build an analytical member for a physical one by itself: the analytical case passes
# only when an own element's analytical row was READ (releases and end fields included);
# named in physical_without_analytical it is not_covered, and an own element the read does
# not account for fails. The end-classification case is not_covered on a document with no
# analytical element. Analytical members read for the own ones are deleted with them.
# What was created is deleted at the end (and a system Revit made for a run, if it outlived
# the run). Types COPIED from a template are not deleted: they stay in the disposable document
# and the cleanup case names them. The document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'rm-analysis'
    Catalog = @(
        @{ Name = 'system_analysis: own duct system read - not_calculated or numbers, reported which'; Tool = 'horizun_plan_mep' }
        @{ Name = 'system_analysis: own pipe system read - not_calculated or numbers, reported which'; Tool = 'horizun_plan_mep' }
        @{ Name = 'system_analysis: non-system id refused by name'; Tool = 'horizun_plan_mep' }
        @{ Name = 'analytical: own beam and column associated (row read with releases) or named without an analytical member'; Tool = 'horizun_query_structure' }
        @{ Name = 'analytical: node gaps measured against the caller tolerance, or named unmeasured'; Tool = 'horizun_query_structure' }
        @{ Name = 'loads: own point load read with case/nature/host'; Tool = 'horizun_query_structure' }
        @{ Name = 'loads: counts, kN units and per-row case and coverage for what the document carries'; Tool = 'horizun_query_structure' }
        @{ Name = 'rm-analysis probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $catalog = @(
            'system_analysis: own duct system read - not_calculated or numbers, reported which',
            'system_analysis: own pipe system read - not_calculated or numbers, reported which',
            'system_analysis: non-system id refused by name',
            'analytical: own beam and column associated (row read with releases) or named without an analytical member',
            'analytical: node gaps measured against the caller tolerance, or named unmeasured',
            'loads: own point load read with case/nature/host',
            'loads: counts, kN units and per-row case and coverage for what the document carries',
            'rm-analysis probes: everything created is deleted')
        # Not $QS/$PM reused as data: PowerShell names are case-insensitive.
        $PlanMepTool = 'horizun_plan_mep'; $StructTool = 'horizun_query_structure'; $DeleteTool = 'horizun_delete_verified'
        $tools = @($PlanMepTool, $PlanMepTool, $PlanMepTool, $StructTool, $StructTool, $StructTool, $StructTool, $DeleteTool)
        $doc = $Ctx.Document; $run = $Ctx.RunId
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        $noLoadKind = 'no typed tool creates a PointLoad: horizun_create_elements has no load kind, and the probes stay on typed tools; the loads read is exercised on the document by the next case'

        # ---- staging (write tier only) --------------------------------------------------------
        $created = New-Object System.Collections.ArrayList
        $ownAnalytical = New-Object System.Collections.ArrayList
        $copiedTypes = New-Object System.Collections.ArrayList
        $why = @{}
        $level = $null; $pipe = $null; $duct = $null; $beam = $null; $column = $null; $pipeSystem = $null; $ductSystem = $null
        if (-not $Ctx.WriteGate) {
            function Types($category) {
                $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
                if (-not $q.data) { return @() }
                return @($q.data.rows | Where-Object { $_.is_element_type })
            }
            function Named($rows, $cand) {
                $parts = @($cand -split ': ', 2)
                $fam = if ($parts.Count -eq 2) { $parts[0] } else { $null }
                return @($rows | Where-Object { [string]$_.type -eq $parts[-1] -and (-not $fam -or [string]$_.family -eq $fam) }) | Select-Object -First 1
            }
            $tplRoot = if ($Ctx.TemplateRoot) { [string]$Ctx.TemplateRoot } else { 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates' }
            # BY NAME: the document's own type of that name, else the same name copied from the
            # year's template. The copy refuses zero or several matches by name and lists what the
            # template carries - that text is the not_covered reason when nothing matched.
            function Bring($category, $candidates, $templates, $key) {
                $have = @(Types $category)
                foreach ($cand in $candidates) { $hit = Named $have $cand; if ($hit) { return $hit } }
                $paths = @($templates | ForEach-Object { Join-Path $tplRoot $_ } | Where-Object { Test-Path -LiteralPath $_ })
                if ($paths.Count -eq 0) { $why[$key] = 'no template under ' + $tplRoot + ': ' + ($templates -join ', '); return $null }
                foreach ($tpl in $paths) {
                    foreach ($cand in $candidates) {
                        $k = $run + '-rm-' + $key + '-' + (((Split-Path $tpl -Leaf) + $cand) -replace '[^A-Za-z0-9]', '')
                        $r = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); category = $category
                                type_names = @($cand); duplicate_types = 'use_destination' } $k
                        if ($r.stage -eq 'apply' -and -not $r.answer.isError) {
                            $hit = Named @(Types $category) $cand
                            if ($hit) { [void]$copiedTypes.Add([long]$hit.element_id); return $hit }
                            $why[$key] = "copied '$cand' from $(Split-Path $tpl -Leaf) but no type of that name reads back"
                        }
                        else { $why[$key] = (Split-Path $tpl -Leaf) + " '" + $cand + "': " + (Short $r.answer) }
                    }
                }
                return $null
            }
            function Create($elements, $key) {
                $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @($elements) } ($run + '-rm-' + $key)
                if ($r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) {
                    $row = @($r.answer.data.rows) | Select-Object -First 1
                    if ($row -and $row.element_id) { [void]$created.Add([long]$row.element_id); return [long]$row.element_id }
                }
                $why[$key] = 'create ' + $key + ': ' + (Short $r.answer)
                return $null
            }
            # The system Revit made for a run, read off the run's own connectors: one id, or none.
            function SystemOf($id) {
                $q = & $Ctx.Call 'horizun_query_model' @{ element_ids = @($id); include_mep = $true; include_links = $false }
                $row = if ($q.data) { @($q.data.rows) | Select-Object -First 1 } else { $null }
                $ids = @($row.mep.connectors | Where-Object { $_.system -and $_.system.id } | ForEach-Object { [long]$_.system.id } | Select-Object -Unique)
                if ($ids.Count -eq 1) { return $ids[0] }
                return $null
            }

            $E = 93000.0; $X = 1130000.0; $Y = 0.0; $Z = $E + 2500.0
            $mepTemplates = @('English\Systems-Default_Metric.rte', 'English\Plumbing-Default_Metric.rte', 'English\Mechanical-Default_Metric.rte', 'English\Default-Multi-Discipline_Metric.rte')
            $strTemplates = @('English\Structural Analysis-DefaultMetric.rte', 'English\DefaultMetric.rte')
            $pipeType = Bring 'OST_PipeCurves' @('Pipe Types: Default', 'Pipe Types: Standard') $mepTemplates 'pipetype'
            $pipeSysType = Bring 'OST_PipingSystem' @('Domestic Cold Water', 'Hydronic Supply') $mepTemplates 'pipesystype'
            $ductType = Bring 'OST_DuctCurves' @('Rectangular Duct: Radius Elbows / Taps', 'Rectangular Duct: Mitered Elbows / Taps') $mepTemplates 'ducttype'
            $ductSysType = Bring 'OST_DuctSystem' @('Supply Air') $mepTemplates 'ductsystype'
            $beamType = Bring 'OST_StructuralFraming' @('M_Concrete-Rectangular Beam: 300 x 600mm', 'M_W Shapes: W310X38.7') $strTemplates 'beamtype'
            $colType = Bring 'OST_StructuralColumns' @('M_Concrete-Rectangular-Column: 300 x 450mm', 'M_W Shapes-Column: W250X49.1') $strTemplates 'coltype'

            $level = Create @(@{ kind = 'level'; name = "HZ_RM_$run"; elevation = $E }) 'level'
            if ($level -and $pipeType -and $pipeSysType) {
                $pipe = Create @(@{ kind = 'pipe'; start = @($X, $Y, $Z); end = @(($X + 6000), $Y, $Z); diameter = 50; level_id = $level
                                    type_id = [long]$pipeType.element_id; system_type_id = [long]$pipeSysType.element_id }) 'pipe'
            }
            if ($level -and $ductType -and $ductSysType) {
                $duct = Create @(@{ kind = 'duct'; start = @($X, ($Y + 3000), $Z); end = @(($X + 6000), ($Y + 3000), $Z); width = 400; height = 300; level_id = $level
                                    type_id = [long]$ductType.element_id; system_type_id = [long]$ductSysType.element_id }) 'duct'
            }
            if ($level -and $beamType) {
                $beam = Create @(@{ kind = 'structural_framing'; start = @($X, ($Y + 8000), ($E + 3000)); end = @(($X + 6000), ($Y + 8000), ($E + 3000)); level_id = $level
                                    type_id = [long]$beamType.element_id; structural_type = 'Beam' }) 'beam'
            }
            if ($level -and $colType) {
                $column = Create @(@{ kind = 'structural_column'; point = @(($X + 6000), ($Y + 8000), $E); level_id = $level; type_id = [long]$colType.element_id }) 'column'
            }
            if ($pipe) { $pipeSystem = SystemOf $pipe; if (-not $pipeSystem) { $why['pipesys'] = "the own pipe $pipe carries no single system on its connectors (query_model include_mep)" } }
            if ($duct) { $ductSystem = SystemOf $duct; if (-not $ductSystem) { $why['ductsys'] = "the own duct $duct carries no single system on its connectors (query_model include_mep)" } }
        }
        function Why($keys) { $t = @($keys | Where-Object { $why.ContainsKey($_) } | ForEach-Object { $why[$_] }); if ($t.Count) { $t -join ' | ' } else { 'staging incomplete' } }

        # ==== 1 and 2: system_analysis on the own systems ======================================
        function ReadSystem($index, $what, $runId, $sysId, $keys) {
            if ($Ctx.WriteGate) { Case $catalog[$index] $tools[$index] 'not_covered' 'the write tier is closed for this run: no own system to read'; return }
            if (-not $runId -or -not $sysId) { Case $catalog[$index] $tools[$index] 'not_covered' ("no own $what system: " + (Why $keys)); return }
            $a = & $Ctx.Call $PlanMepTool @{ operation = 'system_analysis'; target_document = $doc; element_ids = @($sysId)
                    limits = @{ max_velocity_m_s = 10; max_pressure_loss_pa = 500 } }
            if ($a.isError -or -not $a.data) { Case $catalog[$index] $tools[$index] 'fail' ('system_analysis: ' + (Short $a)); return }
            $row = @($a.data.systems | Where-Object { [long]$_.id -eq $sysId }) | Select-Object -First 1
            if (-not $row) { Case $catalog[$index] $tools[$index] 'fail' "no row for the own $what system $sysId"; return }
            $status = [string]$row.calculation_status; $verdict = [string]$row.verdict
            $judged = @('within_limits', 'beyond_limits', 'no_limits_given', 'limits_partly_unmeasured')
            # The API calls a badly connected system's calculated values invalid: nothing is read from it.
            $invalid = @('not_well_connected', 'connectivity_unreadable')
            $calc = ($status -eq 'calculated' -or $status -eq 'flow_only')
            $nothingRead = ($status -eq 'not_calculated' -or $status -eq 'unreadable') -or ($calc -and $invalid -contains $verdict)
            $problems = @()
            if ([int]$a.data.system_count -ne 1) { $problems += "system_count $($a.data.system_count) for one id" }
            if (-not $row.coverage) { $problems += 'the system row carries no coverage word' }
            if ($nothingRead) {
                # One system nothing was read from: never judged, no critical path, never the whole truth.
                if ($judged -contains $verdict) { $problems += "a $status system was judged '$verdict'" }
                if ($row.coverage -and [string]$row.coverage -ne 'unreadable') { $problems += "row coverage '$($row.coverage)' for a system nothing was read from, expected unreadable" }
                if ($a.data.coverage -and [string]$a.data.coverage.coverage -ne 'unreadable') { $problems += "reply coverage '$($a.data.coverage.coverage)' for one system nothing was read from, expected unreadable" }
                if ($null -ne $row.critical_path) { $problems += 'a critical path was published for a system nothing was read from' }
                if ($null -ne $row.critical_path_pressure_loss_pa) { $problems += 'a path loss was published for a system nothing was read from' }
            }
            elseif ($calc) {
                if ($verdict -eq 'within_limits' -and @($row.unmeasured_limits).Count -gt 0) { $problems += 'within_limits with limits unmeasured: ' + (@($row.unmeasured_limits) -join ',') }
                if ($judged -contains $verdict -and $null -eq $row.critical_path_sections) { $problems += "verdict '$verdict' with no critical path count" }
            }
            else { $problems += "calculation_status '$status' is not one of calculated/flow_only/not_calculated/unreadable" }
            if (-not $a.data.coverage) { $problems += 'no coverage block' }
            if ($problems.Count) { Case $catalog[$index] $tools[$index] 'fail' ($problems -join '; '); return }
            $first = @($row.critical_path) | Select-Object -First 1
            $numbers = if ($first) { "; first section flow $($first.flow_l_s) l/s, velocity $($first.velocity_m_s) m/s, loss $($first.pressure_loss_pa) Pa" } else { '' }
            Case $catalog[$index] $tools[$index] 'pass' ("$status (level $($row.calculation_level), well connected $($row.is_well_connected)): verdict $verdict, coverage $($row.coverage), $($row.critical_path_sections) critical-path section(s), path loss $($row.critical_path_pressure_loss_pa) Pa$numbers")
        }
        ReadSystem 0 'duct' $duct $ductSystem @('level', 'ducttype', 'ductsystype', 'duct', 'ductsys')
        ReadSystem 1 'pipe' $pipe $pipeSystem @('level', 'pipetype', 'pipesystype', 'pipe', 'pipesys')

        # ==== 3: a run's id is not a system - refused by name, nothing read ====================
        $notSystem = if ($pipe) { $pipe } elseif ($level) { $level } else { $null }
        if (-not $notSystem) { Case $catalog[2] $tools[2] 'not_covered' $(if ($Ctx.WriteGate) { 'the write tier is closed for this run: no own non-system element' } else { 'no own element staged: ' + (Why @('level')) }) }
        else {
            $n = & $Ctx.Call $PlanMepTool @{ operation = 'system_analysis'; target_document = $doc; element_ids = @($notSystem) }
            if ($n.isError -and (Short $n) -match 'not a MechanicalSystem or PipingSystem') { Case $catalog[2] $tools[2] 'pass' ('refused: ' + (Short $n)) }
            else { Case $catalog[2] $tools[2] 'fail' ('expected a refusal naming the class, got: ' + $(if ($n.isError) { Short $n } else { "a reply with $($n.data.system_count) system(s)" })) }
        }

        # ==== 5: the whole document's analytical read with a caller tolerance (after staging, so
        # an analytical member Revit made for the own beam is on it) ===========================
        $whole = & $Ctx.Call $StructTool @{ mode = 'analytical'; target_document = $doc; tolerance_mm = 5; max_rows = 500 }
        if ($whole.isError -or -not $whole.data) { Case $catalog[4] $tools[4] 'fail' ('analytical read: ' + (Short $whole)) }
        else {
            $w = $whole.data; $problems = @()
            if ([string]$w.tolerance_source -ne 'caller' -or [math]::Abs([double]$w.tolerance_mm - 5) -gt 0.001) { $problems += "tolerance $($w.tolerance_mm) from '$($w.tolerance_source)', expected 5 from the caller" }
            # StructuralCoverage.Reason publishes { what, why, element_id? }: read the field, not the JSON text.
            $reasonWhat = if ($w.coverage) { @($w.coverage.reasons | ForEach-Object { [string]$_.what }) } else { @() }
            if ($w.node_gaps_measured -eq $true) { if ($null -eq $w.member_ends_beyond_tolerance -or $null -eq $w.member_ends_supported) { $problems += 'gaps measured but member_ends_beyond_tolerance or member_ends_supported is null' } }
            elseif ($w.node_gaps_measured -eq $false) {
                if ($null -ne $w.member_ends_beyond_tolerance) { $problems += 'gaps not measured but a count is given' }
                if ($reasonWhat -notcontains 'node_gaps') { $problems += 'gaps not measured and no coverage reason names node_gaps' }
            }
            else { $problems += "node_gaps_measured is '$($w.node_gaps_measured)'" }
            if (-not $w.coverage) { $problems += 'no coverage block' }
            if (-not $w.physical_without_analytical) { $problems += 'no physical_without_analytical block' }
            if ($problems.Count) { Case $catalog[4] $tools[4] 'fail' ($problems -join '; ') }
            elseif ([int]$w.matched -eq 0) { Case $catalog[4] $tools[4] 'not_covered' ("no analytical member or panel in the document (no typed tool creates one): the end classification ran on nothing; physical without analytical: $($w.physical_without_analytical.count) of $($w.physical_without_analytical.checked)") }
            else { Case $catalog[4] $tools[4] 'pass' ("$($w.matched) analytical element(s); gaps measured: $($w.node_gaps_measured), ends beyond 5 mm: $($w.member_ends_beyond_tolerance), supported: $($w.member_ends_supported); physical without analytical: $($w.physical_without_analytical.count) of $($w.physical_without_analytical.checked)") }
        }

        # ==== 4: the own beam and column are accounted for ======================================
        $own = @(@($beam, $column) | Where-Object { $_ })
        if ($Ctx.WriteGate) { Case $catalog[3] $tools[3] 'not_covered' 'the write tier is closed for this run: no own beam or column' }
        elseif ($own.Count -eq 0) { Case $catalog[3] $tools[3] 'not_covered' ('no own beam or column: ' + (Why @('level', 'beamtype', 'coltype', 'beam', 'column'))) }
        else {
            $ar = & $Ctx.Call $StructTool @{ mode = 'analytical'; target_document = $doc; element_ids = $own; tolerance_mm = 5 }
            if ($ar.isError -or -not $ar.data) { Case $catalog[3] $tools[3] 'fail' ('analytical read of the own members: ' + (Short $ar)) }
            else {
                $pwa = $ar.data.physical_without_analytical
                $without = @($pwa.ids | ForEach-Object { [long]$_ })
                $unmatched = @($ar.data.unmatched_ids | ForEach-Object { [long]$_ })
                $problems = @(); $said = @(); $rowsRead = 0
                foreach ($id in $own) {
                    if ($unmatched -contains $id) { $problems += "own $id is in unmatched_ids: the read did not see a structural element it staged"; continue }
                    if ($without -contains $id) { $said += "$id named without an analytical member"; continue }
                    if ([string]$pwa.coverage -ne 'complete' -or $null -eq $pwa.ids) { $problems += "own $id neither listed nor vouched for (physical_without_analytical coverage '$($pwa.coverage)')"; continue }
                    $rowsFor = if ($whole.data) { @($whole.data.rows | Where-Object { @($_.associated_physical_ids | ForEach-Object { [long]$_ }) -contains $id }) } else { @() }
                    if ($rowsFor.Count -eq 0) {
                        if ($whole.data -and [int]$whole.data.matched -le 500) { $problems += "own $id is vouched associated but no row of the whole analytical read ($($whole.data.matched) element(s), every one on the page) lists it"; continue }
                        $said += "$id associated (its analytical row is not on the first page of $($whole.data.matched))"; continue
                    }
                    $am = $rowsFor[0]
                    if ($am.kind -eq 'member' -and $null -eq $am.member.releases) { $problems += "own $id's analytical member $($am.id) carries no releases block"; continue }
                    if ($am.kind -eq 'member' -and $whole.data.node_gaps_measured -eq $true -and $null -eq $am.node_gaps) { $problems += "own $id's analytical member $($am.id) carries no node_gaps although gaps were measured"; continue }
                    if (-not $am.coverage) { $problems += "own $id's analytical row $($am.id) carries no coverage"; continue }
                    $said += "$id associated to analytical $($am.kind) $($am.id) (coverage $($am.coverage), releases read)"; $rowsRead++; [void]$ownAnalytical.Add([long]$am.id)
                }
                if ($problems.Count) { Case $catalog[3] $tools[3] 'fail' ($problems -join '; ') }
                elseif ($rowsRead -eq 0) { Case $catalog[3] $tools[3] 'not_covered' ('no own element has an analytical row this run could read (Revit 2023+ builds none by itself and no typed tool creates one), so association, releases and end fields were not exercised: ' + ($said -join '; ')) }
                else { Case $catalog[3] $tools[3] 'pass' ($said -join '; ') }
            }
        }

        # ==== 6 and 7: loads ====================================================================
        Case $catalog[5] $tools[5] 'not_covered' $noLoadKind
        $ld = & $Ctx.Call $StructTool @{ mode = 'loads'; target_document = $doc; max_rows = 500 }
        if ($ld.isError -or -not $ld.data) { Case $catalog[6] $tools[6] 'fail' ('loads read: ' + (Short $ld)) }
        else {
            $l = $ld.data; $problems = @()
            $counts = $l.counts
            if ($null -eq $counts -or $null -eq $counts.point -or $null -eq $counts.line -or $null -eq $counts.area) { $problems += 'counts must name point, line and area' }
            elseif ([int]$l.matched -ne ([int]$counts.point + [int]$counts.line + [int]$counts.area)) { $problems += "matched $($l.matched) is not point+line+area" }
            if ([string]$l.units.point_force -ne 'kN' -or [string]$l.units.area_force -ne 'kN/m2') { $problems += "units are $($l.units | ConvertTo-Json -Compress)" }
            foreach ($r in @($l.rows)) {
                if (-not $r.load_case) { $problems += "load $($r.id) has no load_case block" }
                if (-not $r.coverage) { $problems += "load $($r.id) has no coverage" }
                elseif ([string]$r.coverage -ne 'complete' -and @($r.unread).Count -eq 0) { $problems += "load $($r.id) is '$($r.coverage)' with nothing named unread" }
            }
            if (-not $l.coverage) { $problems += 'no coverage block' }
            $converted = @(@($l.rows) | Where-Object { ($_.point -and $null -ne $_.point.force_kn) -or ($_.line -and $null -ne $_.line.force1_kn_m) -or ($_.area -and $null -ne $_.area.force1_kn_m2) })
            foreach ($r in $converted) { if (-not $r.vector_frame) { $problems += "load $($r.id) publishes force components with no vector_frame" } }
            if ($problems.Count) { Case $catalog[6] $tools[6] 'fail' ($problems -join '; ') }
            elseif ([int]$l.matched -eq 0) { Case $catalog[6] $tools[6] 'not_covered' 'the document carries no point, line or area load (no typed tool creates one): no load row was read and no kN conversion ran' }
            elseif ($converted.Count -eq 0) { Case $catalog[6] $tools[6] 'not_covered' "$($l.matched) load(s), but no row on the page published a converted force: the kN conversion was not exercised" }
            else { Case $catalog[6] $tools[6] 'pass' ("$($counts.point) point, $($counts.line) line, $($counts.area) area load(s) read in kN units; by case: $($l.by_load_case | ConvertTo-Json -Compress)") }
        }

        # ---- cleanup: members, runs, level; then any system Revit made that outlived its run ----
        if ($Ctx.WriteGate) { Case $catalog[7] $tools[7] 'not_covered' 'the write tier is closed for this run: nothing was created' }
        else {
            # Analytical members read for the own ones go first (the list is reversed).
            $ids = @(@($created) + @($ownAnalytical) | Select-Object -Unique)
            $kept = if ($copiedTypes.Count) { '; types copied from a template stay in the disposable document: ' + (@($copiedTypes) -join ',') } else { '' }
            if ($ids.Count -eq 0) { Case $catalog[7] $tools[7] 'not_covered' 'nothing was created' }
            else {
                [array]::Reverse($ids)
                $del = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 500 } ($run + '-rm-cleanup')
                if (-not ($del.stage -eq 'apply' -and -not $del.answer.isError)) { Case $catalog[7] $tools[7] 'fail' ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
                else {
                    $systems = @(@($pipeSystem, $ductSystem) | Where-Object { $_ })
                    $left = @()
                    if ($systems.Count) {
                        $q = & $Ctx.Call 'horizun_query_model' @{ element_ids = $systems; include_links = $false }
                        $left = if ($q.data) { @($q.data.rows | Where-Object { $_.element_id } | ForEach-Object { [long]$_.element_id }) } else { @() }
                    }
                    if ($left.Count -eq 0) { Case $catalog[7] $tools[7] 'pass' ("deleted $($ids.Count) created ids; the run systems ($($systems -join ',')) went with their runs$kept") }
                    else {
                        $del2 = & $Ctx.Apply $DeleteTool @{ target_document = $doc; mode = 'ids'; ids = $left; id_cap = 500 } ($run + '-rm-cleanup-systems')
                        if ($del2.stage -eq 'apply' -and -not $del2.answer.isError) { Case $catalog[7] $tools[7] 'pass' ("deleted $($ids.Count) created ids, then the $($left.Count) run system(s) Revit had kept: $($left -join ',')$kept") }
                        else { Case $catalog[7] $tools[7] 'fail' ('run systems left in the disposable document: ' + ($left -join ',') + ' - ' + (Short $del2.answer)) }
                    }
                }
            }
        }
        return $cases
    }
}
