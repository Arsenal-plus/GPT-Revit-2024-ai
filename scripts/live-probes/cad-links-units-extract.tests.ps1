#Requires -Version 5.1
# Exercises cad-links-units-extract.probes.ps1 WITHOUT Revit. The fakes follow the reply
# shapes the code builds (ManageCadLinksCommand add: element_id, units_check {verdict,
# applied}, host_verified, application.state, failed_postconditions; QueryCadCommand profile:
# response_mode, layers_profiled; the compact refusal text) - shapes from the code, to be
# held against the first live run.
#
# The fake bridge models what was measured: the drawing is drawn in inch (header), a NEW link
# type takes the forced unit (W1), a link of an already-linked file keeps the first type's unit
# (W3), and with no DWG reader nothing can be measured.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'cad-links-units-extract.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'cad-links-units-extract' }
if (-not $module) { 'module did not register'; exit 1 }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-cu-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

# fixed: the bridge after the fix. legacy: as it was (literal verified, extract throws, no compact).
# noreader: fixed, on a runner with no accoreconsole.
function New-Ctx([bool]$gate, [string]$behaviour = 'fixed', [bool]$exportWorks = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; nextId = 7000; linkedFiles = @{} }
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
            $path = [string]$arguments.file_path
            $asked = $arguments.units
            $reused = $state.linkedFiles.ContainsKey($path)
            $typeUnit = if ($reused) { $state.linkedFiles[$path] } elseif ($asked) { $asked } else { 'inch' }
            if (-not $reused) { $state.linkedFiles[$path] = $typeUnit }
            if ($behaviour -eq 'legacy') {
                $data = [pscustomobject]@{ element_id = $state.nextId; host_verified = $true; application = [pscustomobject]@{ state = 'verified_applied' } }
                return @{ stage = 'apply'; answer = @{ isError = $false; data = $data } }
            }
            if (-not $asked) { $verdict = 'not_requested'; $applied = 'inch' }
            elseif ($behaviour -eq 'noreader') { $verdict = 'unconfirmable'; $applied = $null }
            elseif ($typeUnit -eq $asked) { $verdict = $(if ($asked -eq 'inch') { 'agrees' } else { 'applied_header_differs' }); $applied = $asked }
            else { $verdict = 'not_applied'; $applied = 'inch' }
            $holds = @('not_requested', 'agrees', 'applied_header_differs') -contains $verdict
            $failed = if ($verdict -eq 'not_applied') { @('units_not_applied') } elseif (-not $holds) { @('units_unconfirmable') } else { $null }
            $st = if ($holds) { 'verified_applied' } elseif ($verdict -eq 'not_applied') { 'partial' } else { 'uncertain' }
            $data = [pscustomobject]@{ element_id = $state.nextId
                                       units_check = [pscustomobject]@{ verdict = $verdict; requested = $asked; applied = $applied; declared_by_link = 'inch' }
                                       host_verified = $holds; failed_postconditions = $failed
                                       application = [pscustomobject]@{ state = $st } }
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
    Expect 'six cases, named as catalogued' ($r.Count -eq 6 -and @($r | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
    Expect 'all pass against the fixed bridge' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
    Expect 'a fresh copy and a reused original were both linked forced' ($ctx.State.applies -contains 'cu-add-forced-new' -and $ctx.State.applies -contains 'cu-add-forced-reused')
    Expect 'every link is deleted' ($ctx.State.applies -contains 'cu-cleanup')

    $ctx = New-Ctx $false 'legacy'
    $r = @(& $module.Run $ctx)
    Expect 'legacy: a literal verified with no units_check FAILS the applied case' ($r[1].Outcome -eq 'fail')
    Expect 'legacy: a literal verified_applied on a reused type FAILS' ($r[2].Outcome -eq 'fail')
    Expect 'legacy: the view-scoped extract that throws FAILS' ($r[3].Outcome -eq 'fail' -and $r[3].Detail -match 'DetailLevel')
    Expect 'legacy: an oversized profile FAILS' ($r[4].Outcome -eq 'fail')
    Expect 'legacy: compact not refused by name FAILS' ($r[5].Outcome -eq 'fail')

    $ctx = New-Ctx $false 'noreader'
    $r = @(& $module.Run $ctx)
    Expect 'no DWG reader: the two forced cases are unverified, never pass or fail' ($r[1].Outcome -eq 'unverified' -and $r[2].Outcome -eq 'unverified' -and $r[0].Outcome -eq 'pass')

    $ctx = New-Ctx $false 'fixed' $false
    $r = @(& $module.Run $ctx)
    Expect 'no DWG exported: five not_covered and the refusal still measured' ($r.Count -eq 6 -and @($r[0..4] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $r[5].Outcome -eq 'pass')

    $ctx = New-Ctx $true
    $r = @(& $module.Run $ctx)
    Expect 'closed write tier: nothing applied, refusal still measured' ($r.Count -eq 6 -and $ctx.State.applies.Count -eq 0 -and @($r[0..4] | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $r[5].Outcome -eq 'pass')
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all cad-links-units-extract probe tests passed'
