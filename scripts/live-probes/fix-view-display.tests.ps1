#Requires -Version 5.1
# Exercises fix-view-display.probes.ps1 WITHOUT Revit. The fakes follow the reply shapes
# the code builds (FixPlanimetryCommand: state / rows[].verified; audit: findings[] with
# rule_id, status, element_ids and observed {field, value} as PlanimetryRules builds it;
# FixPlanimetryDisplay's rehearsal refusals: 'only when the cited finding is about
# <property>' checked BEFORE the template, then 'CONTROLS <label>') - shapes from the
# code, to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'fix-view-display.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'fix-view-display' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [string]$initial = 'Medium', [bool]$fixWorks = $true, [bool]$clearWorks = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; level = $initial; discipline = 'Mechanical'
                governed = $false; cleared = $false; refusals = New-Object System.Collections.Generic.List[string] }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_planimetry') {
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } }
        }
        if ($tool -eq 'horizun_audit_planimetry') {
            $rule = $arguments.requirement_set.rules[0]
            $field = $rule.assertion.field
            $have = if ($field -eq 'discipline') { $state.discipline } else { $state.level }
            $findings = @()
            if ($have -ne $rule.assertion.value) {
                $findings = @([pscustomobject]@{ rule_id = $rule.id; status = 'failed'; requirement_set = 'horizun-probe-view-display'
                                                  requirement_set_version = '1.0.0'; requirement_set_sha256 = 'abc'; entity_kind = 'view'
                                                  element_ids = @(900); view_id = 900; observed = [pscustomobject]@{ field = $field; value = $have } })
            }
            return @{ isError = $false; data = [pscustomobject]@{ finding_set_fingerprint = 'fp12345678'; findings = $findings } }
        }
        if ($tool -eq 'horizun_fix_planimetry') {
            $a = $arguments.actions[0]
            if ($a.Contains('discipline') -and $a.finding.observed.field -ne 'discipline') {
                $state.refusals.Add('licence')
                return @{ isError = $true; text = "set_view_display sets discipline only when the cited finding is about discipline: finding '$($a.finding.rule_id)' asserts '$($a.finding.observed.field)'. Nothing was written." }
            }
            if ($state.governed) {
                $state.refusals.Add('template')
                $label = if ($a.Contains('discipline')) { 'Discipline' } else { 'Detail Level' }
                return @{ isError = $true; text = "view template 'HZ_VD_TPL_t1' (901) CONTROLS $label on view_id 900: an assignment would be overwritten by the template. Nothing was written." }
            }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } } }
        if ($tool -eq 'horizun_manage_views') {
            $first = $arguments.actions[0]
            if ($first.operation -eq 'create_template') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]@{ tpl = 901 }; rows = @() } } } }
            if ($first.operation -eq 'apply_template' -and $first.template_view_id -eq -1) {
                if (-not $clearWorks) { return @{ stage = 'apply'; answer = @{ isError = $true; text = 'refused' } } }
                $state.cleared = $true
            }
            if ($first.operation -eq 'set_template_controls') { $state.governed = $true }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ verified = $true; element_id = 900 }) } } }
        }
        if ($tool -eq 'horizun_fix_planimetry') {
            $a = $arguments.actions[0]
            if ($a.operation -ne 'set_view_display' -or $a.view_id -ne 900 -or -not $a.finding.requirement_set_sha256) {
                return @{ stage = 'apply'; answer = @{ isError = $true; text = 'bad fix request' } }
            }
            if (-not $fixWorks) { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ state = 'failed_rolled_back'; rows = @() } } } }
            if ($a.Contains('detail_level')) { $state.level = $a.detail_level }
            if ($a.Contains('discipline')) { $state.discipline = $a.discipline }
            $row = [pscustomobject]@{ index = 0; operation = 'set_view_display'; target_id = 900; verified = $true
                                      postconditions = [pscustomobject]@{ all_verified = $true } }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ state = 'verified_applied'; rows = @($row) } } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected tool ' + $tool } }
    }.GetNewClosure()
    return [pscustomobject]@{ Document = 'HZ_WRITE'; RunId = 't1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$failures = 0
function Expect($label, $cond) { if (-not $cond) { Write-Host "FAIL: $label"; $script:failures++ } else { Write-Host "ok: $label" } }

$ctx = New-Ctx $false 'Medium'
$r = @(& $module.Run $ctx)
Expect 'seven cases' ($r.Count -eq 7)
Expect 'all pass on a Medium / Mechanical view' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Expect 'the duplicate''s template is cleared right after staging' ($ctx.State.cleared -and $ctx.State.applies[1] -eq 'vd-clear-tpl')
Expect 'fixed to Fine and Coordination' ($ctx.State.level -eq 'Fine' -and $ctx.State.discipline -eq 'Coordination')
Expect 'one licence refusal, then two template refusals, all in rehearsals' (($ctx.State.refusals -join ',') -eq 'licence,template,template')
Expect 'cleanup ran' ($ctx.State.applies -contains 'vd-cleanup')

$ctx = New-Ctx $false 'Fine'
$r = @(& $module.Run $ctx)
Expect 'a Fine view is corrected to Coarse' ($ctx.State.level -eq 'Coarse' -and @($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)

$ctx = New-Ctx $false 'Medium' $false
$r = @(& $module.Run $ctx)
Expect 'a failed fix is a fail, the rest not_covered' ($r.Count -eq 7 -and $r[1].Outcome -eq 'fail' -and @($r[2..6] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0)

$ctx = New-Ctx $false 'Medium' $true $false
$r = @(& $module.Run $ctx)
Expect 'a template that cannot be cleared: all not_covered, the duplicate deleted' ($r.Count -eq 7 -and @($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies -contains 'vd-cleanup')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' ($r.Count -eq 7 -and @($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all fix-view-display probe tests passed'
