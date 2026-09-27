#Requires -Version 5.1
# Exercises rm-analysis.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The reply shapes are FROM THE CODE (PlanMepSystemAnalysis.cs, QueryStructureAnalytical.cs,
# QueryStructureCommand.Ok, StructuralCoverage.Declare/Reason, MepFacts.Json's connector.system),
# to be held against the first live run - none of them is measured yet.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'rm-analysis.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'rm-analysis' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }
function Obj($h) { [pscustomobject]$h }
# StructuralCoverage.Declare: { coverage, measured, not_measured, is_whole_truth, reasons[] },
# each reason StructuralCoverage.Reason: { what, why, element_id? }.
function Cov($word, [object[]]$reasons = @()) {
    $whole = $word -eq 'complete'
    Obj @{ coverage = $word; measured = $(if ($whole) { 1 } else { 0 }); not_measured = $(if ($whole) { 0 } else { 1 }); is_whole_truth = $whole; reasons = $reasons }
}

# A fake template root: the probe copies BY NAME from files that exist.
$tpl = Join-Path $env:TEMP ('hz-rm-tpl-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $tpl 'English') | Out-Null
foreach ($f in 'Systems-Default_Metric.rte', 'Structural Analysis-DefaultMetric.rte') { Set-Content -LiteralPath (Join-Path $tpl "English\$f") -Value 'fake' }

function TypeRow($id, $family, $type) { Obj @{ element_id = $id; is_element_type = $true; family = $family; type = $type } }
function New-State {
    $script:nextId = 5000; $script:byKind = @{}; $script:deleted = @(); $script:sent = @{}; $script:copies = @()
    $script:typesInDoc = @{
        'OST_PipeCurves' = @((TypeRow 201 'Pipe Types' 'Default'))
        'OST_PipingSystem' = @((TypeRow 202 'Piping System' 'Domestic Cold Water'))
        'OST_DuctCurves' = @((TypeRow 203 'Rectangular Duct' 'Radius Elbows / Taps'))
        'OST_DuctSystem' = @((TypeRow 204 'Duct System' 'Supply Air'))
        'OST_StructuralFraming' = @((TypeRow 205 'M_Concrete-Rectangular Beam' '300 x 600mm'))
        'OST_StructuralColumns' = @((TypeRow 206 'M_Concrete-Rectangular-Column' '300 x 450mm'))
    }
    $script:systemsLeft = @()
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; class = $classification; calculation_level = 'None'; calculation_status = 'not_calculated'
        is_well_connected = $true; verdict = 'not_calculated'; verdict_means = 'the system type calculates None: nothing was judged.'; critical_path = $null
        critical_path_pressure_loss_pa = $null; unmeasured_limits = @('max_velocity_m_s', 'max_pressure_loss_pa'); coverage = 'unreadable'
        coverage_reason = 'not_calculated: the system type calculates nothing this read may judge, so no number was read.' } }
    $script:aggCoverage = $null
    $script:pwa = { Obj @{ checked = 2; count = 2; ids = @($script:byKind['structural_framing'], $script:byKind['structural_column']); coverage = 'complete' } }
    $script:wholeRows = { @(Obj @{ id = 9001; kind = 'member'; associated_physical_ids = @(); association = 'none'; coverage = 'complete'; node_gaps = @(); member = (Obj @{ releases = (Obj @{}) }) }) }
    $script:loads = { @(Obj @{ id = 900; kind = 'point'; load_case = (Obj @{ id = 50; name = 'DL1'; number = 1 }); nature = 'Dead'; host_id = $null; vector_frame = 'project'; point = (Obj @{ position_mm = @(0, 0, 0); force_kn = @(0, 0, -10) }); unread = @(); coverage = 'complete' }) }
    $script:unmatched = @()
    $script:gaps = { @{ node_gaps_measured = $true; member_ends_beyond_tolerance = 0; member_ends_supported = 0; coverage = (Cov 'complete') } }
}

$fakeCall = {
    param($tool, $arguments)
    switch ($tool) {
        'horizun_query_model' {
            if ($arguments.categories) {
                $rows = $script:typesInDoc[$arguments.categories[0]]
                return Reply (Obj @{ rows = @($rows) }) $false ''
            }
            if ($arguments.include_mep) {
                # MepFacts.Json: every connector names the system it belongs to.
                $id = [long]$arguments.element_ids[0]
                $sys = if ($id -eq $script:byKind['pipe']) { 700 } elseif ($id -eq $script:byKind['duct']) { 701 } else { $null }
                $conn = @(1, 2) | ForEach-Object { Obj @{ id = $_; domain = 'piping'; system = $(if ($sys) { Obj @{ id = $sys; name = 'S 1' } } else { $null }) } }
                return Reply (Obj @{ rows = @(Obj @{ element_id = $id; mep = (Obj @{ connectors = $conn }) }) }) $false ''
            }
            # the leftover-systems read: present ids come back as rows
            return Reply (Obj @{ rows = @($script:systemsLeft | ForEach-Object { Obj @{ element_id = $_ } }) }) $false ''
        }
        'horizun_plan_mep' {
            $id = [long]$arguments.element_ids[0]
            if ($id -ne 700 -and $id -ne 701) { return Reply $null $true "element_ids: $id is a Pipe, not a MechanicalSystem or PipingSystem. system_analysis reads systems; nothing was read." }
            $cls = if ($id -eq 700) { 'PipingSystem' } else { 'MechanicalSystem' }
            $r = & $script:systemRow $id $cls
            # One system: the call's word is the row's (StructuralCoverage.Weakest of one), its reason the row's cause.
            $word = if ($script:aggCoverage) { $script:aggCoverage } elseif ($r.coverage) { [string]$r.coverage } else { 'partial' }
            $rs = if ($word -eq 'complete') { @() } else { @(Obj @{ what = 'system'; why = [string]$r.coverage_reason; element_id = $id }) }
            return Reply (Obj @{ operation = 'system_analysis'; systems = @($r); system_count = 1; systems_beyond_limits = 0
                                 coverage = (Cov $word $rs) }) $false ''
        }
        'horizun_query_structure' {
            if ($arguments.mode -eq 'loads') {
                $rows = @(& $script:loads)
                return Reply (Obj @{ mode = 'loads'; matched = $rows.Count; returned = $rows.Count; rows = $rows; counts = (Obj @{ point = $rows.Count; line = 0; area = 0 }); by_load_case = (Obj @{ DL1 = $rows.Count })
                                     units = (Obj @{ point_force = 'kN'; point_moment = 'kN*m'; line_force = 'kN/m'; area_force = 'kN/m2' }); coverage = (Cov 'complete') }) $false ''
            }
            if ($arguments.element_ids) {
                return Reply (Obj @{ mode = 'analytical'; matched = 0; rows = @(); physical_without_analytical = (& $script:pwa); unmatched_ids = $script:unmatched
                                     tolerance_mm = 5; tolerance_source = 'caller'; node_gaps_measured = $true; member_ends_beyond_tolerance = 0; coverage = (Cov 'complete') }) $false ''
            }
            $g = & $script:gaps
            $rows = @(& $script:wholeRows)
            return Reply (Obj @{ mode = 'analytical'; matched = $rows.Count; rows = $rows; tolerance_mm = $arguments.tolerance_mm; tolerance_source = 'caller'
                                 node_gaps_measured = $g.node_gaps_measured; member_ends_beyond_tolerance = $g.member_ends_beyond_tolerance; member_ends_supported = $g.member_ends_supported; coverage = $g.coverage
                                 physical_without_analytical = (Obj @{ checked = 0; count = 0; ids = @(); coverage = 'complete' }) }) $false ''
        }
    }
    return Reply $null $true "unexpected call $tool"
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++; $script:byKind[$arguments.elements[0].kind] = $script:nextId
            return @{ stage = 'apply'; answer = (Reply (Obj @{ rows = @(Obj @{ element_id = $script:nextId }) }) $false '') }
        }
        'horizun_copy_between_documents' {
            $script:copies += , $arguments
            $parts = @($arguments.type_names[0] -split ': ', 2)
            $script:typesInDoc[$arguments.category] = @($script:typesInDoc[$arguments.category]) + @(TypeRow 800 $(if ($parts.Count -eq 2) { $parts[0] } else { 'X' }) $parts[-1])
            return @{ stage = 'apply'; answer = (Reply (Obj @{}) $false '') }
        }
        'horizun_delete_verified' { $script:deleted += , @($arguments.ids); return @{ stage = 'apply'; answer = (Reply (Obj @{}) $false '') } }
    }
    return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") }
}

function RunWith($id, [bool]$gate = $false) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; TemplateRoot = $tpl; RunId = $id; WriteGate = $gate; Call = $fakeCall; Apply = $fakeApply }
    $by = @{}; foreach ($c in @(& $module.Run $ctx)) { if ($by.ContainsKey($c.Name)) { $by[$c.Name + '#dup'] = $c } else { $by[$c.Name] = $c } }
    return $by
}
$n = @($module.Catalog | ForEach-Object { $_.Name })

try {
    # ---- the plain run: both systems not calculated, members named without analytical ----
    New-State
    $by = RunWith 't1'
    Check 'every catalogued case is reported exactly once' (($by.Count -eq $n.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    Check 'a not_calculated duct system passes and says not_calculated' (($by[$n[0]].Outcome -eq 'pass') -and ($by[$n[0]].Detail -match '^not_calculated'))
    Check 'the pipe system is read by the id on the pipe''s connectors' (($by[$n[1]].Outcome -eq 'pass'))
    Check 'a pipe id is refused by name' (($by[$n[2]].Outcome -eq 'pass') -and ($by[$n[2]].Detail -match 'not a MechanicalSystem'))
    Check 'own beam and column named without an analytical member are not_covered, saying so' (($by[$n[3]].Outcome -eq 'not_covered') -and ($by[$n[3]].Detail -match 'named without'))
    Check 'node gaps measured with the caller tolerance pass' ($by[$n[4]].Outcome -eq 'pass')
    Check 'the own point load is not_covered with the reason' (($by[$n[5]].Outcome -eq 'not_covered') -and ($by[$n[5]].Detail -match 'no typed tool creates a PointLoad'))
    Check 'the loads read passes on counts, kN and per-row coverage' (($by[$n[6]].Outcome -eq 'pass') -and ($by[$n[6]].Detail -match '1 point'))
    Check 'cleanup deletes the 5 created ids in reverse and the systems went with the runs' (($by[$n[7]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 1) -and (@($script:deleted[0]).Count -eq 5) -and (@($script:deleted[0])[0] -eq 5005))
    Check 'types in the document are used by name - nothing copied' ($script:copies.Count -eq 0)
    Check 'the duct is rectangular with width and height, on its own system type' (($script:sent['t1-rm-duct'].elements[0].width -eq 400) -and ($script:sent['t1-rm-duct'].elements[0].system_type_id -eq 204))

    # ---- a system nothing was read from: never complete, never a critical path ----
    New-State
    $script:aggCoverage = 'complete'
    $b16 = RunWith 't16'
    Check 'a lone not_calculated system under a complete reply fails' (($b16[$n[0]].Outcome -eq 'fail') -and ($b16[$n[0]].Detail -match "reply coverage 'complete'"))
    New-State
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; calculation_level = 'All'; calculation_status = 'calculated'; is_well_connected = $false; verdict = 'not_well_connected'
        verdict_means = 'Revit reports the system NOT well connected: ...'; critical_path = @(Obj @{ number = 1; velocity_m_s = 12.0 }); critical_path_pressure_loss_pa = $null
        unmeasured_limits = @('max_velocity_m_s', 'max_pressure_loss_pa'); coverage = 'unreadable'; coverage_reason = 'not_well_connected: ...' } }
    $b17 = RunWith 't17'
    Check 'a not_well_connected system that still publishes a critical path fails' (($b17[$n[1]].Outcome -eq 'fail') -and ($b17[$n[1]].Detail -match 'critical path was published'))
    New-State
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; calculation_level = 'All'; calculation_status = 'calculated'; is_well_connected = $false; verdict = 'not_well_connected'
        verdict_means = 'Revit reports the system NOT well connected: ...'; critical_path = $null; critical_path_pressure_loss_pa = $null
        unmeasured_limits = @('max_velocity_m_s', 'max_pressure_loss_pa'); coverage = 'unreadable'; coverage_reason = 'not_well_connected: ...' } }
    $b19 = RunWith 't19'
    Check 'a not_well_connected system read as nothing passes, saying so' (($b19[$n[1]].Outcome -eq 'pass') -and ($b19[$n[1]].Detail -match 'verdict not_well_connected, coverage unreadable'))

    # ---- a not_calculated system judged within_limits is a fail ----
    New-State
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; calculation_level = 'None'; calculation_status = 'not_calculated'; verdict = 'within_limits'; unmeasured_limits = @() } }
    $b2 = RunWith 't2'
    Check 'a not_calculated system judged within_limits fails' (($b2[$n[0]].Outcome -eq 'fail') -and ($b2[$n[0]].Detail -match 'judged'))

    # ---- calculated with numbers: pass with the numbers; within_limits with unmeasured limits fails ----
    New-State
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; calculation_level = 'All'; calculation_status = 'calculated'; is_well_connected = $true; verdict = 'within_limits'; unmeasured_limits = @()
        critical_path_sections = 2; critical_path_pressure_loss_pa = 41.2; critical_path = @(Obj @{ number = 1; flow_l_s = 0.3; velocity_m_s = 1.1; pressure_loss_pa = 20.6 }); coverage = 'complete'; coverage_reason = $null } }
    $b3 = RunWith 't3'
    Check 'a calculated system passes with its numbers named' (($b3[$n[1]].Outcome -eq 'pass') -and ($b3[$n[1]].Detail -match 'velocity 1.1'))
    New-State
    $script:systemRow = { param($id, $classification) Obj @{ id = $id; calculation_status = 'calculated'; is_well_connected = $true; verdict = 'within_limits'; unmeasured_limits = @('max_velocity_m_s'); critical_path_sections = 1
        coverage = 'partial'; coverage_reason = '1 section limit(s) unmeasured (unmeasured_limits).' } }
    $b4 = RunWith 't4'
    Check 'within_limits with a limit unmeasured fails' (($b4[$n[1]].Outcome -eq 'fail') -and ($b4[$n[1]].Detail -match 'unmeasured'))

    # ---- an own member the read does not account for is a fail ----
    New-State
    $script:pwa = { Obj @{ checked = 1; count = 0; ids = @(); coverage = 'complete' } }
    $script:unmatched = @(5004)
    $b5 = RunWith 't5'
    Check 'an own beam in unmatched_ids fails' (($b5[$n[3]].Outcome -eq 'fail') -and ($b5[$n[3]].Detail -match 'unmatched'))

    # ---- associated: the analytical row is read with its releases ----
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $script:wholeRows = { @((Obj @{ id = 9101; kind = 'member'; associated_physical_ids = @(5004); association = 'associated'; coverage = 'complete'
                                    member = (Obj @{ releases = (Obj @{ start = (Obj @{ type = 'Fixed' }) }) }) }),
                            (Obj @{ id = 9102; kind = 'member'; associated_physical_ids = @(5005); association = 'associated'; coverage = 'complete'; member = (Obj @{ releases = $null }) })) }
    $b6 = RunWith 't6'
    Check 'an associated member without a releases block fails, naming it' (($b6[$n[3]].Outcome -eq 'fail') -and ($b6[$n[3]].Detail -match 'releases'))
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $script:wholeRows = { @(5004, 5005 | ForEach-Object { Obj @{ id = 9100 + $_; kind = 'member'; associated_physical_ids = @($_); coverage = 'complete'; node_gaps = @(); member = (Obj @{ releases = (Obj @{}) }) } }) }
    $b7 = RunWith 't7'
    Check 'associated beam and column pass with their analytical ids' (($b7[$n[3]].Outcome -eq 'pass') -and ($b7[$n[3]].Detail -match 'associated to analytical member 14104'))
    Check 'the analytical members read for the own ones are deleted first' ((@($script:deleted[0]) -contains 14104) -and (@($script:deleted[0]) -contains 14105) -and (@($script:deleted[0])[0] -eq 14105))

    # ---- node gaps unmeasured without a reason is a fail; with a reason, a pass ----
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null; coverage = (Cov 'partial') } }
    $b8 = RunWith 't8'
    Check 'gaps unmeasured with no reason naming node_gaps fail' (($b8[$n[4]].Outcome -eq 'fail') -and ($b8[$n[4]].Detail -match 'node_gaps'))
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null
        coverage = (Cov 'partial' @(Obj @{ what = 'node_gaps'; why = 'ends x segments exceeds 50000000 checks; narrow with element_ids. Node gaps were NOT measured, which is not the same as none.' })) } }
    $b9 = RunWith 't9'
    Check 'gaps unmeasured and named pass' ($b9[$n[4]].Outcome -eq 'pass')
    New-State
    $script:gaps = { @{ node_gaps_measured = $false; member_ends_beyond_tolerance = $null
        coverage = (Cov 'partial' @(Obj @{ what = 'supports'; why = '1 boundary condition(s) would not give their geometry - node_gaps may be off' })) } }
    $b18 = RunWith 't18'
    Check 'a reason that says node_gaps only in its why text does not name the unmeasured check' (($b18[$n[4]].Outcome -eq 'fail') -and ($b18[$n[4]].Detail -match 'no coverage reason names node_gaps'))

    # ---- a type absent from the document is copied BY NAME from the template ----
    New-State
    $script:typesInDoc['OST_PipeCurves'] = @((TypeRow 299 'Pipe Types' 'Some Other'))
    $b10 = RunWith 't10'
    $copy = @($script:copies | Where-Object { $_.category -eq 'OST_PipeCurves' }) | Select-Object -First 1
    Check 'the pipe type is copied by name with its category, never the first type' ($copy -and ($copy.type_names[0] -eq 'Pipe Types: Default') -and ($copy.source_path -match 'Systems-Default_Metric.rte$') -and ($script:sent['t10-rm-pipe'].elements[0].type_id -eq 800))
    Check 'a copied type is not deleted and the cleanup case names it as kept' (($b10[$n[7]].Outcome -eq 'pass') -and ($b10[$n[7]].Detail -match 'stay in the disposable document: 800') -and (@($script:deleted[0]) -notcontains 800))

    # ---- a leftover run system is deleted in a second call ----
    New-State
    $script:systemsLeft = @(700)
    $b11 = RunWith 't11'
    Check 'a run system Revit kept is deleted after the runs' (($b11[$n[7]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 2) -and (@($script:deleted[1])[0] -eq 700))

    # ---- vouched associated but absent from a whole read that fit on one page: a fail ----
    New-State
    $script:pwa = { Obj @{ checked = 2; count = 0; ids = @(); coverage = 'complete' } }
    $b13 = RunWith 't13'
    Check 'an own member vouched associated that no row of a one-page read lists fails' (($b13[$n[3]].Outcome -eq 'fail') -and ($b13[$n[3]].Detail -match 'no row of the whole analytical read'))

    # ---- nothing to read is not_covered, never a pass ----
    New-State
    $script:wholeRows = { @() }
    $script:loads = { @() }
    $b14 = RunWith 't14'
    Check 'no analytical element makes the end classification not_covered' (($b14[$n[4]].Outcome -eq 'not_covered') -and ($b14[$n[4]].Detail -match 'no analytical member'))
    Check 'no load makes the loads read not_covered' (($b14[$n[6]].Outcome -eq 'not_covered') -and ($b14[$n[6]].Detail -match 'no point, line or area load'))
    New-State
    $script:loads = { @(Obj @{ id = 901; kind = 'line'; load_case = (Obj @{ id = 50; name = 'DL1'; number = 1 }); unread = @(); coverage = 'complete'; line = (Obj @{ force1_kn_m = $null }) }) }
    $b15 = RunWith 't15'
    Check 'loads with no converted force on the page are not_covered' (($b15[$n[6]].Outcome -eq 'not_covered') -and ($b15[$n[6]].Detail -match 'kN conversion was not exercised'))

    # ---- closed write tier: staging cases not_covered, the document reads still run ----
    New-State
    $b12 = RunWith 't12' $true
    $staged = @(0, 1, 2, 3, 5, 7 | ForEach-Object { $b12[$n[$_]].Outcome })
    Check 'a closed write tier reports the staged cases not_covered and still reads gaps and loads' ((@($staged | Where-Object { $_ -ne 'not_covered' }).Count -eq 0) -and ($b12[$n[4]].Outcome -eq 'pass') -and ($b12[$n[6]].Outcome -eq 'pass') -and ($script:sent.Count -eq 0))
}
finally { Remove-Item -LiteralPath $tpl -Recurse -Force -ErrorAction SilentlyContinue }

if ($fails) { "rm-analysis tests: $fails FAILED"; exit 1 } else { 'rm-analysis tests: ALL PASS'; exit 0 }
