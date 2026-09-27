#Requires -Version 5.1
# Exercises framing-curtain.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply whose
# replies copy the shapes the code builds - SHAPES FROM THE CODE, TO BE HELD AGAINST THE FIRST
# LIVE RUN: CurtainWallSummary / CurtainCeilingSummary (plan.sources[].method, pieces[].role,
# carrier.action / type_id / span, layers[].plane_mm, hangers[].base_mm / top_mm, not_built),
# VerifyCurtainWalls (evidence.sources[].pieces[].grid.layout_vert, carrier.type_ok /
# inserts_checked / inserts_changed / deleted / deleted_with_it[_measured]), VerifyRemoved +
# VerifyCurtainRestores (evidence.carrier_restores[].restored / wall_id / recreated_with_new_id /
# inserts_changed / not_restored_because), VerifyCurtainCeilings (evidence.hanger_recheck.
# stations_checked / not_at_support) and ManageCurtainCommand.Read (counts, host_kind,
# grid1_angle_deg) - where ModelEditRunner places that read result is exactly what the first
# live run must confirm (the probe looks in data, data.read and data.result).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'framing-curtain.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'framing-curtain' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }
# No template on disk: every type comes from the fake query, and a missing one stays missing.
$noTemplates = Join-Path ([IO.Path]::GetTempPath()) ('hz-frc-tests-' + [guid]::NewGuid().ToString('N'))

function New-State {
    $script:nextId = 7000; $script:wallApplies = 0; $script:sent = @{}; $script:deleted = $null; $script:removeTargets = @()
    $script:noGlazing = $false; $script:w1Restore = @{ restored = $true; inserts_changed = 0; why = $null }; $script:notAtSupport = @()
}

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { @(@{ element_id = 501; is_element_type = $true; family = 'Curtain Wall'; type = 'Curtain Wall 1' },
                            @{ element_id = 502; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' },
                            @{ element_id = 503; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 150mm' }) }
            'OST_Doors' { @(@{ element_id = 504; is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm' }) }
            'OST_Roofs' { if ($script:noGlazing) { @() } else { @(@{ element_id = 505; is_element_type = $true; family = 'Sloped Glazing'; type = 'Sloped Glazing' }) } }
            'OST_Floors' { @(@{ element_id = 506; is_element_type = $true; family = 'Floor'; type = 'Generic 300mm' }) }
            'OST_Ceilings' { @(@{ element_id = 507; is_element_type = $true; family = 'Compound Ceiling'; type = '600 x 600mm Grid' }) }
            default { @() }
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) }) $false ''
    }
    if ($tool -eq 'horizun_framing' -and $arguments.operation -eq 'wall') {
        $sid = [long]@($arguments.element_ids)[0]
        if ($sid -eq 7002) {
            $src = [pscustomobject]@{ source_id = 7002; status = 'planned'; method = 'curtain'; length_mm = 6000.0; height_mm = 3000.0; core_offset_mm = 0.0
                openings = @([pscustomobject]@{ id = '7004'; start = 2042.5; end = 2957.5; sill = 0.0; head = 2134.0 })
                pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; type_id = 501 }, [pscustomobject]@{ i = 1; role = 'curtain_segment'; type_id = 501 }, [pscustomobject]@{ i = 2; role = 'curtain_header'; type_id = 501 })
                skipped = @(); carrier = [pscustomobject]@{ action = 'trim'; original_type_id = 502; type_id = 503; span = @(2042.5, 2957.5); opening_id = '7004' } }
        }
        else {
            $src = [pscustomobject]@{ source_id = $sid; status = 'planned'; method = 'curtain'; openings = @(); pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; type_id = 501 })
                skipped = @(); carrier = [pscustomobject]@{ action = 'delete'; original_type_id = 502; type_id = $null; span = $null; replaced_by = 'every curtain_segment piece'
                    deleted_with_it = [pscustomobject]@{ count = 0; by_category = [pscustomobject]@{}; ids = @() } } }
        }
        return Reply ([pscustomobject]@{ dry_run = $true; operation = 'wall'; plan = [pscustomobject]@{ method = 'curtain'; sources = @($src); member_count = @($src.pieces).Count } }) $false ''
    }
    if ($tool -eq 'horizun_framing' -and $arguments.operation -eq 'ceiling') {
        $hangers = @(0..2 | ForEach-Object { [pscustomobject]@{ i = 2 + $_; type_id = 501; from = @(1190000, (600 + 1200 * $_)); to = @(1194800, (600 + 1200 * $_)); base_mm = 100480.0; top_mm = 100700.0 } })
        return Reply ([pscustomobject]@{ dry_run = $true; operation = 'ceiling'; plan = [pscustomobject]@{ method = 'curtain'; member_count = 5; sources = @([pscustomobject]@{
            source_id = 7006; status = 'planned'; method = 'curtain'; top_face_mm = 100450.0; sketch_loops = 1; openings_not_cut = @()
            layers = @([pscustomobject]@{ i = 0; type_id = 505; plane_mm = 100450.0; angle_deg = 0 }, [pscustomobject]@{ i = 1; type_id = 505; plane_mm = 100480.0; angle_deg = 90 })
            hanger_base_mm = 100480.0; hanger_direction_deg = 0.0; hangers = $hangers; not_built = @(); hanger_supports = [pscustomobject]@{ 'host:7005' = 3 } }) } }) $false ''
    }
    if ($tool -eq 'horizun_manage_curtain') {
        return Reply ([pscustomobject]@{ element_id = [long]$arguments.element_id; grid_index = 0; host_kind = 'FootPrintRoof'
            counts = [pscustomobject]@{ u_lines = 11; v_lines = 4; mullions = 40; panels = 60 }; grid1_angle_deg = 0.0; grid2_angle_deg = 90.0 }) $false ''
    }
    return Reply $null $true "unexpected call $tool"
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) }) $false '') }
        }
        'horizun_framing' {
            $op = $arguments.operation; $sid = [long]@($arguments.element_ids)[0]
            if ($op -eq 'remove') {
                $script:removeTargets += @($arguments.element_ids)
                $restores = @()
                foreach ($id in @($arguments.element_ids)) {
                    if ([long]$id -eq 7002) {
                        $restores += [pscustomobject]@{ carrier_id = 7002; action_at_apply = 'trim'; wall_id = 7002; restored = $script:w1Restore.restored; line_deviation_mm = 0.0
                                                        inserts_checked = 1; inserts_changed = $script:w1Restore.inserts_changed; not_restored_because = $script:w1Restore.why }
                    }
                    elseif ([long]$id -eq 7003) {
                        $restores += [pscustomobject]@{ carrier_id = 7003; action_at_apply = 'delete'; wall_id = 9999; recreated_with_new_id = $true; restored = $true
                                                        line_deviation_mm = 0.0; base_deviation_mm = 0.0; top_deviation_mm = 0.0; not_in_record = 'mark, comments, phase, workset and other instance parameters' }
                    }
                }
                $data = [pscustomobject]@{ operation = 'remove'; transaction_status = 'Committed'; postconditions = [pscustomobject]@{ all_verified = $true }
                    application = [pscustomobject]@{ state = 'verified_applied' }
                    evidence = [pscustomobject]@{ removed_ids = @(8001, 8002, 8003); cascaded_ids = @(); cascade_measured_in_rehearsal = @(); foreign_copies_kept = 0; sources = @($arguments.element_ids) } }
                if ($restores.Count -gt 0) { $data.evidence | Add-Member -NotePropertyName carrier_restores -NotePropertyValue $restores }
                return @{ stage = 'apply'; answer = (Reply $data $false '') }
            }
            if ($op -eq 'ceiling') {
                $pieces = @([pscustomobject]@{ i = 0; role = 'curtain_layer'; id = 9101; plane_deviation_mm = 0.0; footprint_deviation_mm = 0.2; slope_defining_edges = 0
                                grid = [pscustomobject]@{ grid1 = [pscustomobject]@{ lines = 11; layout = 1; direction_deg = 0.0; planned_direction_deg = 0.0 }; grid2 = [pscustomobject]@{ lines = 4; layout = 1 } } },
                            [pscustomobject]@{ i = 1; role = 'curtain_layer'; id = 9102; plane_deviation_mm = 0.0; footprint_deviation_mm = 0.2; slope_defining_edges = 0
                                grid = [pscustomobject]@{ grid1 = [pscustomobject]@{ lines = 4; layout = 1; direction_deg = 90.0; planned_direction_deg = 90.0 } } }) +
                          @(2..4 | ForEach-Object { [pscustomobject]@{ i = $_; role = 'curtain_hanger'; id = 9100 + $_; location_deviation_mm = 0.0; base_top_deviation_mm = 0.0; grid = [pscustomobject]@{ vertical_lines = 3 } } })
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'ceiling'; transaction_status = 'Committed'; already_applied = $false
                    application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ hanger_recheck = [pscustomobject]@{ stations_checked = 9; not_at_support = $script:notAtSupport; max_gap_mm = 0.1 }
                        sources = @([pscustomobject]@{ source_id = 7006; already_applied = $false; pieces = $pieces; not_built = @(); member_ids = @(9101..9105) }) } }) $false '') }
            }
            if ($sid -eq 7002) {
                $script:wallApplies++
                $again = $script:wallApplies -gt 1
                $grid = [pscustomobject]@{ vertical_lines = 5; horizontal_lines = 0; layout_vert = 1; layout_vert_text = 'Fixed Distance'; spacing_mm = 406.4; spacing_problems = @() }
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $again
                    application = [pscustomobject]@{ state = $(if ($again) { 'no_op' } else { 'verified_applied' }) }; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = 7002; already_applied = $again
                        pieces = @(0..2 | ForEach-Object { [pscustomobject]@{ i = $_; role = $(if ($_ -eq 2) { 'curtain_header' } else { 'curtain_segment' }); id = 9001 + $_; location_deviation_mm = 0.0; grid = $grid } })
                        carrier = [pscustomobject]@{ id = 7002; action = 'trim'; type_ok = $true; curve_deviation_mm = 0.0; inserts_checked = 1; inserts_changed = 0 }; piece_ids = @(9001, 9002, 9003) }) } }) $false '') }
            }
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = $false
                application = [pscustomobject]@{ state = 'verified_applied' }; postconditions = [pscustomobject]@{ all_verified = $true }
                evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ source_id = $sid; already_applied = $false; pieces = @([pscustomobject]@{ i = 0; role = 'curtain_segment'; id = 9011 })
                    carrier = [pscustomobject]@{ id = $sid; action = 'delete'; deleted = $true; deleted_with_it = @(); deleted_with_it_measured = @(); inserts_checked = 0; inserts_changed = 0 } }) } }) $false '') }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

function Ctx($runId) { [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; TemplateRoot = $noTemplates; ScratchRoot = (Join-Path $noTemplates 'scratch'); RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply } }
function RunBy($context) { $by = @{}; foreach ($c in @(& $module.Run $context)) { $by[$c.Name] = $c }; $by }
$names = @($module.Catalog | ForEach-Object { $_.Name })

try {
    New-State
    $cases = @(& $module.Run (Ctx 't1'))
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    foreach ($name in $names) { Check ('the happy path passes: ' + $name) ($by[$name].Outcome -eq 'pass') }
    $applySent = $script:sent['t1-frc-apply']
    Check 'the wall apply names the one-door wall, method curtain, the curtain and placeholder types' (($applySent.operation -eq 'wall') -and ($applySent.element_ids[0] -eq 7002) -and
        ($applySent.spec.wall.method -eq 'curtain') -and ($applySent.spec.wall.curtain_type_id -eq 501) -and ($applySent.spec.wall.placeholder_type_id -eq 503))
    Check 'the door is hosted on the staged wall' (($script:sent['t1-frc-door'].elements[0].host_id -eq 7002))
    Check 'the remove names both carriers' ((@($script:removeTargets) -contains 7002) -and (@($script:removeTargets) -contains 7003))
    $ceilSent = $script:sent['t1-frc-ceiling']
    Check 'the ceiling apply sends two sloped glazing layers 30 mm apart and the curtain type as hanger' (($ceilSent.element_ids[0] -eq 7006) -and (@($ceilSent.spec.ceiling.layers).Count -eq 2) -and
        (@($ceilSent.spec.ceiling.layers | Where-Object { $_.type_id -ne 505 }).Count -eq 0) -and ($ceilSent.spec.ceiling.layers[1].offset_mm - $ceilSent.spec.ceiling.layers[0].offset_mm -eq 30) -and ($ceilSent.spec.ceiling.hanger.type_id -eq 501))
    Check 'cleanup deletes the recreated carrier and never the deleted one' ((@($script:deleted) -contains 9999) -and -not (@($script:deleted) -contains 7003) -and (@($script:deleted).Count -eq 6))
    Check 'the manage_curtain case reports the grid angles' ($by[$names[7]].Detail -match 'grid 1 at 0 deg, grid 2 at 90 deg')

    # ---- a carrier whose restore is refused fails the remove case, and the cleanup ----
    New-State; $script:w1Restore = @{ restored = $false; inserts_changed = 0; why = 'its type changed after the apply' }
    $refusedBy = RunBy (Ctx 't2')
    Check 'a refused restore fails the remove case with its reason' (($refusedBy[$names[4]].Outcome -eq 'fail') -and ($refusedBy[$names[4]].Detail -match 'type changed after the apply'))
    Check 'a refused restore fails the cleanup case too' ($refusedBy[$names[8]].Outcome -eq 'fail')

    # ---- the door not back where it was is a fail ----
    New-State; $script:w1Restore = @{ restored = $true; inserts_changed = 1; why = $null }
    $movedBy = RunBy (Ctx 't3')
    Check 'a restored carrier whose door moved fails the remove case' (($movedBy[$names[4]].Outcome -eq 'fail') -and ($movedBy[$names[4]].Detail -match 'door back'))

    # ---- a hanger not at its support fails the ceiling apply ----
    New-State; $script:notAtSupport = @(9103)
    $shortBy = RunBy (Ctx 't4')
    Check 'a hanger not at the support fails the ceiling apply, counted' (($shortBy[$names[6]].Outcome -eq 'fail') -and ($shortBy[$names[6]].Detail -match '1 hanger\(s\) not at the support'))

    # ---- no sloped glazing type: the ceiling and read cases are not_covered, named ----
    New-State; $script:noGlazing = $true
    $bareBy = RunBy (Ctx 't5')
    Check 'without a sloped glazing type the ceiling cases are not_covered with the reason' (($bareBy[$names[5]].Outcome -eq 'not_covered') -and ($bareBy[$names[5]].Detail -match "sloped glazing type ''") -and ($bareBy[$names[6]].Outcome -eq 'not_covered'))
    Check 'without a layer the manage_curtain read is not_covered and the cleanup still passes' (($bareBy[$names[7]].Outcome -eq 'not_covered') -and ($bareBy[$names[8]].Outcome -eq 'pass'))

    # ---- a closed write tier ----
    New-State
    $closed = Ctx 't6'; $closed.WriteGate = $true
    $shut = @(& $module.Run $closed)
    Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
    Check 'a closed write tier names each case''s own tool' ((@($shut | Where-Object { $_.Name -like 'framing curtain probes:*' -and $_.Tool -eq 'horizun_delete_verified' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -like 'manage_curtain*' -and $_.Tool -eq 'horizun_manage_curtain' }).Count -eq 1) -and
        (@($shut | Where-Object { $_.Name -like 'framing curtain wall*' -and $_.Tool -ne 'horizun_framing' }).Count -eq 0))
}
finally {
    Remove-Item -LiteralPath $noTemplates -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fails) { "framing-curtain tests: $fails FAILED"; exit 1 } else { 'framing-curtain tests: ALL PASS'; exit 0 }
