#Requires -Version 5.1
# Exercises cad-plan-storey.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply that answer
# the way the bridge does after the fixes (link_geometry_only, walls at the storey, a summary, apply
# by plan_id, a two-level column type), the way it did in the dry run that found them (coherence_unknown,
# Z = 0, no top_level), and with the write tier closed.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'cad-plan-storey.probes.ps1')
$module = @($script:HzProbeModules | Where-Object { $_.Name -eq 'cad-plan-storey' })[0]

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-cps-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

function New-Fake([bool]$fixed) {
    $state = @{ calls = New-Object System.Collections.Generic.List[string]; fixed = $fixed; deleted = $null; levelB = $null }
    $call = {
        param($tool, $arguments)
        $state.calls.Add('call:' + $tool)
        $ok = { param($data) @{ isError = $false; data = [pscustomobject]$data } }
        switch ($tool) {
            'horizun_query_cad' {
                if ($arguments.mode -eq 'instances') { return (& $ok @{ instances = @([pscustomobject]@{ element_id = 900; declared_units = 'inch' }) }) }
                return (& $ok @{ layers = @([pscustomobject]@{ layer = 'A-GLAZ' }, [pscustomobject]@{ layer = 'A-WALL-____-MCUT' }) })
            }
            'horizun_plan_from_cad' {
                if ($arguments.catalog_check_only) {
                    $top = $arguments.requirement_set.rules[0].top_level
                    $row = if (-not $state.fixed) {
                        [pscustomobject]@{ placement_type = 'TwoLevelsBased'; verdict = 'refused'; problems = @('hosting_incompatible: a TwoLevelsBased family cannot be placed without a host (this mode takes OneLevelBased)') } }
                    elseif ($top) { [pscustomobject]@{ placement_type = 'TwoLevelsBased'; verdict = 'usable_with_warnings'; problems = @() } }
                    else { [pscustomobject]@{ placement_type = 'TwoLevelsBased'; verdict = 'refused'; problems = @('column_top_unstated: ... Declare top_level ...') } }
                    return (& $ok @{ rules = @($row) })
                }
                $z = if ($state.fixed) { 123000.0 } else { 0.0 }
                $elements = @(
                    [pscustomobject]@{ kind = 'wall'; start = @(985000.0, 0.0, $z); end = @(991000.0, 0.0, $z) },
                    [pscustomobject]@{ kind = 'wall'; start = @(991000.0, 0.0, $z); end = @(991000.0, 4000.0, $z) })
                $levelName = $arguments.requirement_set.rules[0].level
                $placement = if ($state.fixed) { [pscustomobject]@{ levels = [pscustomobject]@{ $levelName = [pscustomobject]@{
                    level_elevation_mm = 123000.0; drawn_z_mm = 0.0; rows_whose_drawn_z_was_not_the_storey = 2 } } } } else { $null }
                $coherence = if ($state.fixed) { [pscustomobject]@{ state = 'link_geometry_only'; basis = 'link_geometry_and_host_file'; references_checked = $false } }
                             else { [pscustomobject]@{ state = 'coherence_unknown'; why = 'the_source_set_could_not_be_identified'; remedy = 'read the drawing once' } }
                $data = @{ applicable = [bool]$state.fixed; coherence = $coherence; storey_placement = $placement; plan_id = 'cadplanid:abc'
                           apply_binding = [pscustomobject]@{ plan_fingerprint = 'cadplan:1'; actions_fingerprint = 'cadacts:1' }
                           execute_plan_request = [pscustomobject]@{ actions = @([pscustomobject]@{ key = 'cad-stage-1-batch-1'; arguments = [pscustomobject]@{ elements = $elements } }) } }
                if ($arguments.response_mode -eq 'summary') {
                    $data.execute_plan_request = [pscustomobject]@{ actions = @() }
                    $data['response_mode'] = 'summary'; $data['response_omissions'] = @([pscustomobject]@{ json_pointer = '/execute_plan_request/actions'; total = 1; shown = 0 })
                }
                return (& $ok $data)
            }
            'horizun_apply_cad_plan' {
                if (-not $state.fixed) { return @{ isError = $true; text = 'plan_not_applicable: coherence_unknown.'; data = $null } }
                if ($arguments.dry_run) { return (& $ok @{ rehearsal = [pscustomobject]@{ tokens_by_key = [pscustomobject]@{ 'cad-stage-1-batch-1' = 'tok' } } }) }
                if ($arguments.plan_id -ne 'cadplanid:abc' -or -not $arguments.confirmation_tokens) { return @{ isError = $true; text = 'bad apply'; data = $null } }
                return (& $ok @{ created_verified = 2; stages_failed = 0; provenance_written = 2
                                 stages = @([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7001 }, [pscustomobject]@{ element_id = 7002 }) }) })
            }
            'horizun_query_model' {
                return (& $ok @{ rows = @([pscustomobject]@{ is_element_type = $true; family = 'M_Concrete-Round-Column'; name = '300mm' }) })
            }
        }
        return @{ isError = $true; text = 'unexpected call ' + $tool; data = $null }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.calls.Add('apply:' + $key)
        $ok = { param($data) @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]$data } } }
        switch ($key) {
            'cps-level-a' { return (& $ok @{ rows = @([pscustomobject]@{ element_id = 501 }) }) }
            'cps-level-b' { return (& $ok @{ rows = @([pscustomobject]@{ element_id = 502 }) }) }
            'cps-walls' { return (& $ok @{ rows = @([pscustomobject]@{ element_id = 601 }, [pscustomobject]@{ element_id = 602 }) }) }
            'cps-plans' { return (& $ok @{ aliases = [pscustomobject]@{ pa = 701; pb = 702 } }) }
            'cps-export' {
                Set-Content -LiteralPath (Join-Path $scratch ("HZ_CPS_{0}-plan.dwg" -f 'r1')) -Value 'dwg'
                return (& $ok @{ files_verified = 1 })
            }
            'cps-walls-gone' { return (& $ok @{ deleted = 2 }) }
            'cps-link' { return (& $ok @{ element_id = 900; host_verified = $true }) }
            'cps-cleanup' { $state.deleted = @($arguments.ids); return (& $ok @{ deleted = @($arguments.ids).Count }) }
        }
        return @{ stage = 'dry_run'; answer = @{ isError = $true; text = 'unexpected apply ' + $key; data = $null } }
    }.GetNewClosure()
    return @{ state = $state; call = $call; apply = $apply }
}

function Run-Module($fake, [bool]$gate) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r1'; WriteGate = $gate
                              Call = $fake.call; Apply = $fake.apply }
    $by = @{}
    foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}
function Case($by, $i) { $by[$module.Catalog[$i].Name] }

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

# ---- after the fixes: every case passes ------------------------------------------------------
$f = New-Fake $true
$by = Run-Module $f $false
Check 'every catalogued case is reported' ($by.Count -eq $module.Catalog.Count)
for ($i = 0; $i -lt $module.Catalog.Count; $i++) {
    Check ("fixed: " + $module.Catalog[$i].Name + ' passes') ((Case $by $i).Outcome -eq 'pass')
}
Check 'the apply went through plan_id, never through copied actions' ($f.state.calls -contains 'call:horizun_apply_cad_plan')
Check 'cleanup deletes the built walls, the link, both plans and the levels last' (
    ($f.state.deleted -join ',') -eq '7002,7001,900,701,702,501,502')

# ---- the dry run that found them: #2, #3 and #4 fail, never pass -------------------------------
Get-ChildItem -LiteralPath $scratch -File | Remove-Item
$d = New-Fake $false
$by = Run-Module $d $false
Check 'unfixed: coherence_unknown is a fail' ((Case $by 1).Outcome -eq 'fail' -and (Case $by 1).Detail -match 'coherence_unknown')
Check 'unfixed: walls at Z = 0 on a level 30 m up is a fail' ((Case $by 2).Outcome -eq 'fail')
Check 'unfixed: a column refused with no word of top_level is a fail' ((Case $by 5).Outcome -eq 'fail')
Check 'unfixed: the apply refusal is a fail' ((Case $by 4).Outcome -eq 'fail')

# ---- write tier closed: nothing is called, everything is not_covered --------------------------
$g = New-Fake $true
$by = Run-Module $g $true
Check 'write gate: every case is not_covered' (@($by.Values | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0)
Check 'write gate: nothing was called' ($g.state.calls.Count -eq 0)

Get-ChildItem -LiteralPath $scratch -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
Remove-Item -LiteralPath $scratch
if ($fails) { "cad-plan-storey tests: $fails FAILED"; exit 1 } else { 'cad-plan-storey tests: ALL PASS'; exit 0 }
