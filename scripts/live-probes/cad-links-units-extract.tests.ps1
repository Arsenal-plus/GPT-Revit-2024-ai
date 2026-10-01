#Requires -Version 5.1
# Exercises cad-links-units-extract.probes.ps1 WITHOUT Revit. The fakes follow the reply
# shapes the code builds (ManageCadLinksCommand add: element_id, instance.declared_units,
# units_check {verdict}, host_verified, application.state, failed_postconditions;
# QueryCadCommand profile: response_mode, layers_profiled; the compact refusal text) -
# shapes from the code, to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'cad-links-units-extract.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'cad-links-units-extract' }
if (-not $module) { 'module did not register'; exit 1 }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-cu-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

# fixed: the bridge as it is after the fix. legacy: as it was (literal verified, extract throws, no compact).
function New-Ctx([bool]$gate, [string]$behaviour = 'fixed', [bool]$exportWorks = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; nextId = 7000 }
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_planimetry') {
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } }
        }
        if ($tool -eq 'horizun_cad_extract') {
            if ($behaviour -eq 'legacy' -and $arguments.view_id) { return @{ isError = $true; text = 'InvalidOperationException: DetailLevel is already set.' } }
            return @{ isError = $false; text = '{"layers":[]}'; data = [pscustomobject]@{ layers = @() } }
        }
        if ($tool -eq 'horizun_query_cad') {
            $rm = $arguments.response_mode
            if ($rm -and $arguments.mode -ne 'profile') {
                if ($behaviour -eq 'legacy') { return @{ isError = $true; text = 'additional property response_mode is not allowed' } }
                return @{ isError = $true; text = "response_mode=compact applies to mode=profile only; mode='layers' already bounds its reply (max_rows, offset). Nothing was read." }
            }
            if ($rm -eq 'compact' -and $behaviour -ne 'legacy') {
                return @{ isError = $false; text = ('x' * 12000); data = [pscustomobject]@{ response_mode = 'compact'; layers_profiled = 27 } }
            }
            return @{ isError = $false; text = ('x' * 78000); data = [pscustomobject]@{ layers_profiled = 27 } }
        }
        return @{ isError = $true; text = 'unexpected tool ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_export') {
            if ($exportWorks) { Set-Content -LiteralPath $arguments.output_path -Value 'AC1032' }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } }
        }
        if ($tool -eq 'horizun_delete_verified') { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{}; text = 'ok' } } }
        if ($tool -eq 'horizun_manage_cad_links') {
            $state.nextId++
            $asked = $arguments.units
            $verdict = if (-not $asked) { 'not_requested' } elseif ($asked -eq 'inch') { 'agrees' } else { 'disagrees' }
            $holds = $verdict -ne 'disagrees'
            if ($behaviour -eq 'legacy') {
                $data = [pscustomobject]@{ element_id = $state.nextId; instance = [pscustomobject]@{ declared_units = 'inch' }
                                           host_verified = $true; application = [pscustomobject]@{ state = 'verified_applied' } }
            } else {
                $data = [pscustomobject]@{ element_id = $state.nextId; instance = [pscustomobject]@{ declared_units = 'inch' }
                                           units_check = [pscustomobject]@{ verdict = $verdict; requested = $asked; declared_by_link = 'inch' }
                                           host_verified = $holds
                                           failed_postconditions = $(if ($holds) { $null } else { @('units_disagree') })
                                           application = [pscustomobject]@{ state = $(if ($holds) { 'verified_applied' } else { 'partial' }) } }
            }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = $data } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected tool ' + $tool } }
    }.GetNewClosure()
    $root = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    return [pscustomobject]@{ Document = 'HZ_WRITE'; RunId = 't1'; WriteGate = $gate; ScratchRoot = $root; Call = $call; Apply = $apply; State = $state }
}

$failures = 0
function Expect($label, $cond) { if (-not $cond) { Write-Host "FAIL: $label"; $script:failures++ } else { Write-Host "ok: $label" } }

try {
    $ctx = New-Ctx $false 'fixed'
    $r = @(& $module.Run $ctx)
    Expect 'five cases, named as catalogued' ($r.Count -eq 5 -and @($r | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
    Expect 'all pass against the fixed bridge' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
    Expect 'the forced link asks for millimeter on an inch link' ($ctx.State.applies -contains 'cu-add-forced')
    Expect 'both links are deleted' ($ctx.State.applies -contains 'cu-cleanup')

    $ctx = New-Ctx $false 'legacy'
    $r = @(& $module.Run $ctx)
    Expect 'legacy: the literal verified_applied over a unit disagreement FAILS' ($r[1].Outcome -eq 'fail')
    Expect 'legacy: the view-scoped extract that throws FAILS' ($r[2].Outcome -eq 'fail' -and $r[2].Detail -match 'DetailLevel')
    Expect 'legacy: an oversized profile FAILS' ($r[3].Outcome -eq 'fail')
    Expect 'legacy: compact not refused by name FAILS' ($r[4].Outcome -eq 'fail')

    $ctx = New-Ctx $false 'fixed' $false
    $r = @(& $module.Run $ctx)
    Expect 'no DWG exported: four not_covered and the refusal still measured' ($r.Count -eq 5 -and @($r[0..3] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $r[4].Outcome -eq 'pass')

    $ctx = New-Ctx $true
    $r = @(& $module.Run $ctx)
    Expect 'closed write tier: nothing applied, refusal still measured' ($r.Count -eq 5 -and $ctx.State.applies.Count -eq 0 -and @($r[0..3] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $r[4].Outcome -eq 'pass')
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all cad-links-units-extract probe tests passed'
