#Requires -Version 5.1
# Exercises fix-view-display.probes.ps1 WITHOUT Revit. The fakes follow the reply shapes
# the code builds (FixPlanimetryCommand: state / rows[].verified; audit: findings[] with
# rule_id, status, element_ids, observed) - shapes from the code, to be held against the
# first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'fix-view-display.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'fix-view-display' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [string]$initial = 'Medium', [bool]$fixWorks = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; level = $initial }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_planimetry') {
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } }
        }
        if ($tool -eq 'horizun_audit_planimetry') {
            $want = $arguments.requirement_set.rules[0].assertion.value
            $findings = @()
            if ($state.level -ne $want) {
                $findings = @([pscustomobject]@{ rule_id = 'probe-detail-level'; status = 'failed'; requirement_set = 'horizun-probe-view-display'
                                                  requirement_set_version = '1.0.0'; requirement_set_sha256 = 'abc'; entity_kind = 'view'
                                                  element_ids = @(900); view_id = 900; observed = [pscustomobject]@{ detail_level = $state.level } })
            }
            return @{ isError = $false; data = [pscustomobject]@{ finding_set_fingerprint = 'fp12345678'; findings = $findings } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } } }
        if ($tool -eq 'horizun_manage_views') {
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ verified = $true; element_id = 900 }) } } }
        }
        if ($tool -eq 'horizun_fix_planimetry') {
            $a = $arguments.actions[0]
            if ($a.operation -ne 'set_view_display' -or $a.view_id -ne 900 -or -not $a.finding.requirement_set_sha256) {
                return @{ stage = 'apply'; answer = @{ isError = $true; text = 'bad fix request' } }
            }
            if (-not $fixWorks) { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ state = 'failed_rolled_back'; rows = @() } } } }
            $state.level = $a.detail_level
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
Expect 'three cases' ($r.Count -eq 3)
Expect 'all pass on a Medium view' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Expect 'fixed to Fine' ($ctx.State.level -eq 'Fine')
Expect 'cleanup ran' ($ctx.State.applies -contains 'vd-cleanup')

$ctx = New-Ctx $false 'Fine'
$r = @(& $module.Run $ctx)
Expect 'a Fine view is corrected to Coarse' ($ctx.State.level -eq 'Coarse' -and @($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)

$ctx = New-Ctx $false 'Medium' $false
$r = @(& $module.Run $ctx)
Expect 'a failed fix is a fail, the re-audit not_covered' ($r[1].Outcome -eq 'fail' -and $r[2].Outcome -eq 'not_covered')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' (@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all fix-view-display probe tests passed'
