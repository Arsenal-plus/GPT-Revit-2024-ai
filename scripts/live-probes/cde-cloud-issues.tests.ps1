#Requires -Version 5.1
# Exercises cde-cloud-issues.probes.ps1 WITHOUT Revit or ACC: a fake Call that keeps a
# tiny in-memory ACC project (issues keyed by their [horizun-key:...] marker) and answers
# issues_list / issue_create the way CdeCloudIssues.cs does - rehearsal, token, apply,
# already_exists - well enough to prove the probe reads those fields correctly.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'cde-cloud-issues.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'cde-cloud-issues' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

# Modes: 'ok' behaves; 'nocred' has no APS credential; 'leaky' creates the issue on the DRY RUN;
# 'readonly' lists but refuses every create for lack of data:write; 'notypes' lists no subtype.
function New-Fake([string]$Mode) {
    $state = @{ issues = New-Object System.Collections.ArrayList; calls = New-Object System.Collections.ArrayList; next = 1 }
    $reply = { param($data, $isError, $text) [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $types = @([pscustomobject]@{ id = 't1'; title = 'Coordination'; is_active = $true
                                  subtypes = @([pscustomobject]@{ id = 'st-1'; title = 'Clash'; is_active = $true }) })
    if ($Mode -eq 'notypes') { $types = @() }
    $call = {
        param($tool, $a)
        [void]$state.calls.Add($a)
        if ($tool -ne 'horizun_cde_cloud') { throw "unexpected tool $tool" }
        if ($Mode -eq 'nocred') {
            return & $reply $null $true 'Autodesk Platform Services credentials are not configured. Set HORIZUN_APS_ACCESS_TOKEN ... No request was made.'
        }
        switch ($a.operation) {
            'issues_list' {
                $rows = @($state.issues | Where-Object { -not $a.external_key -or $_.external_key -eq $a.external_key })
                return & $reply ([pscustomobject]@{ issues = $rows; count = $rows.Count; issue_types = $types; root_cause_categories = @()
                                                    coverage_complete = $true; problems = @() }) $false ''
            }
            'issue_create' {
                if ($Mode -eq 'readonly') {
                    return & $reply $null $true ('issue_create needs data:write and the token from aps-token.json carries only [data:read]. ' +
                                                 "Sign in again asking 'data:read data:write'. Nothing was written.")
                }
                $existing = @($state.issues | Where-Object { $_.external_key -eq $a.external_key }) | Select-Object -First 1
                if ($null -ne $existing) {
                    return & $reply ([pscustomobject]@{ state = 'already_exists'; issue_id = $existing.issue_id; host_verified = $true }) $false ''
                }
                if ($a.issue.issue_type_id -ne 'st-1') {
                    return & $reply $null $true ("issue.issue_type_id '" + $a.issue.issue_type_id + "' is not an active issue SUBTYPE of this project. Nothing was written.")
                }
                $dry = -not ($a.ContainsKey('dry_run') -and $a.dry_run -eq $false)
                if ($dry) {
                    if ($Mode -eq 'leaky') { [void]$state.issues.Add([pscustomobject]@{ issue_id = 'leak'; external_key = $a.external_key }) }
                    return & $reply ([pscustomobject]@{ state = 'rehearsed'; plan = [pscustomobject]@{ method = 'POST'; path = '/issues' }
                                                        confirmation_token = 'tok-1'; idempotency_scan = [pscustomobject]@{ complete = $true } }) $false ''
                }
                if ($a.confirmation_token -ne 'tok-1') { return & $reply $null $true 'token mismatch. Nothing was written.' }
                $id = 'iss-' + $state.next; $state.next++
                [void]$state.issues.Add([pscustomobject]@{ issue_id = $id; external_key = $a.external_key })
                return & $reply ([pscustomobject]@{ state = 'applied'; host_verified = $true; issue_id = $id; web_url = 'https://acc.example.test/' + $id }) $false ''
            }
        }
        throw "unexpected operation $($a.operation)"
    }.GetNewClosure()
    return @{ state = $state; call = $call }
}
function Ctx($fake, [bool]$gate, $project, $write) {
    [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 'r-1'; WriteGate = $gate
                       AccProjectId = $project; AccIssueWrite = $write; Call = $fake.call; Apply = { throw 'the probe must not use the Revit Apply helper' } }
}
function Run-Probe($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$n = @($module.Catalog | ForEach-Object { $_.Name })
$savedProject = $env:HORIZUN_PROBE_ACC_PROJECT_ID; $savedWrite = $env:HORIZUN_PROBE_ACC_ISSUE_WRITE
$env:HORIZUN_PROBE_ACC_PROJECT_ID = $null; $env:HORIZUN_PROBE_ACC_ISSUE_WRITE = $null
try {
    # No project named: nothing is called, every case is not_covered.
    $f0 = New-Fake 'ok'
    $b0 = Run-Probe (Ctx $f0 $true $null $null)
    Check 'every catalogued case is reported without a project' (@($n | Where-Object { -not $b0.ContainsKey($_) }).Count -eq 0)
    Check 'no project named: all not_covered and no call made' (@($b0.Values | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $f0.state.calls.Count -eq 0)

    # A behaving project, write tier closed: list and rehearsal pass, the apply is not_covered, nothing applied.
    $f1 = New-Fake 'ok'
    $b1 = Run-Probe (Ctx $f1 $true 'proj-guid' '1')
    Check 'every catalogued case is reported' (@($n | Where-Object { -not $b1.ContainsKey($_) }).Count -eq 0)
    Check 'issues_list passes' ($b1[$n[0]].Outcome -eq 'pass')
    Check 'the dry run passes and creates nothing' ($b1[$n[1]].Outcome -eq 'pass' -and $f1.state.issues.Count -eq 0)
    Check 'a closed write tier leaves the apply not_covered' ($b1[$n[2]].Outcome -eq 'not_covered')
    Check 'no call sent dry_run=false' (@($f1.state.calls | Where-Object { $_.ContainsKey('dry_run') -and $_.dry_run -eq $false }).Count -eq 0)
    Check 'no call carries a credential-looking argument' (@($f1.state.calls | Where-Object { $_.Keys -match 'token$|secret|password' -and -not ($_.Keys -contains 'confirmation_token') }).Count -eq 0)

    # Open write tier WITHOUT the opt-in: still not_covered.
    $f2 = New-Fake 'ok'
    $b2 = Run-Probe (Ctx $f2 $false 'proj-guid' $null)
    Check 'without the explicit opt-in the apply stays not_covered' ($b2[$n[2]].Outcome -eq 'not_covered' -and $f2.state.issues.Count -eq 0)

    # Open write tier WITH the opt-in: applied once, the keyed retry finds it.
    $f3 = New-Fake 'ok'
    $b3 = Run-Probe (Ctx $f3 $false 'proj-guid' '1')
    Check 'opted in: the apply passes and the retry does not duplicate' ($b3[$n[2]].Outcome -eq 'pass' -and $f3.state.issues.Count -eq 1)
    if ($b3[$n[2]].Outcome -ne 'pass') { "    $($b3[$n[2]].Detail)" }

    # No credential: nothing can be rehearsed, so the list AND the dry run are not_covered.
    $f4 = New-Fake 'nocred'
    $b4 = Run-Probe (Ctx $f4 $false 'proj-guid' '1')
    Check 'no credential: the list is not_covered' ($b4[$n[0]].Outcome -eq 'not_covered')
    Check 'no credential: the dry run is not_covered, not a pass' ($b4[$n[1]].Outcome -eq 'not_covered')
    Check 'no credential: the apply is not_covered' ($b4[$n[2]].Outcome -eq 'not_covered')

    # A subtype was read and the create is refused for lack of data:write: that is a failure.
    $f7 = New-Fake 'readonly'
    $b7 = Run-Probe (Ctx $f7 $true 'proj-guid' $null)
    Check 'a data:write refusal of a real subtype fails the dry run' ($b7[$n[1]].Outcome -eq 'fail')

    # No subtype readable: the fabricated subtype's own refusal is the rehearsal's proof.
    $f8 = New-Fake 'notypes'
    $b8 = Run-Probe (Ctx $f8 $true 'proj-guid' $null)
    Check 'no subtype: the refusal naming the fabricated subtype passes' ($b8[$n[1]].Outcome -eq 'pass')
    if ($b8[$n[1]].Outcome -ne 'pass') { "    $($b8[$n[1]].Detail)" }

    # A server whose dry run writes is caught.
    $f5 = New-Fake 'leaky'
    $b5 = Run-Probe (Ctx $f5 $true 'proj-guid' $null)
    Check 'a dry run that creates an issue fails' ($b5[$n[1]].Outcome -eq 'fail')

    # The environment names the project when the context does not.
    $env:HORIZUN_PROBE_ACC_PROJECT_ID = 'proj-from-env'
    $f6 = New-Fake 'ok'
    $b6 = Run-Probe (Ctx $f6 $true $null $null)
    Check 'HORIZUN_PROBE_ACC_PROJECT_ID names the project' ($b6[$n[0]].Outcome -eq 'pass' -and $f6.state.calls[0].project_id -eq 'proj-from-env')
}
finally {
    $env:HORIZUN_PROBE_ACC_PROJECT_ID = $savedProject; $env:HORIZUN_PROBE_ACC_ISSUE_WRITE = $savedWrite
}
if ($fails) { "cde-cloud-issues probe tests: $fails FAILED"; exit 1 } else { 'cde-cloud-issues probe tests: ALL PASS'; exit 0 }
