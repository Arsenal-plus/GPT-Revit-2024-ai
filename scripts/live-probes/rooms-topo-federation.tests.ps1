#Requires -Version 5.1
# Exercises rooms-topo-federation.probes.ps1 WITHOUT Revit. Shapes from the code
# (FederationLevelRules.cs / FederationCheckCommand.cs), to be held against the first
# live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'rooms-topo-federation.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'rooms-topo-federation' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $src = Join-Path $env:TEMP ('hz-fake-host-' + [guid]::NewGuid().ToString('N') + '.rvt')
    Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $s = @{ Mode = $mode; Deleted = @(); Src = $src; Linked = $false }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = $s.Src }) }) }
            'horizun_federation_check' {
                if ($a.rules.levels_match -is [hashtable] -and $a.rules.levels_match.ContainsKey('tol')) {
                    return & $reply $null $true "The federation rules were refused: levels_match: unknown key 'tol'. Known: tolerance_mm."
                }
                $rows = @(); $links = @()
                if ($s.Linked) {
                    $state = if ($s.Mode -eq 'differs') { 'differs' } else { 'matches' }
                    $matching = if ($s.Mode -eq 'differs') { 2 } else { 3 }
                    $rows += [pscustomobject]@{ instance_id = 901; title = 'HZ_LVLSRC'; state = $state; levels_compared = 3; levels_matching = $matching; mismatches = @(); host_levels_not_in_link = @() }
                    $links += [pscustomobject]@{ instance_id = 901; title = 'HZ_LVLSRC'; loaded = $true }
                }
                $verdict = if ($s.Mode -eq 'differs') { 'fails' } else { 'passes' }
                return & $reply ([pscustomobject]@{ verdict = $verdict; levels = $rows; links = $links; summary = [pscustomobject]@{ links_levels_differ = 0; links_levels_not_read = 0 } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_manage_links' { $s.Linked = $true; return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 }) }
            'horizun_delete_verified' { $s.Deleted = @($a.ids); $s.Linked = $false; return & $ok ([pscustomobject]@{ ok = $true }) }
        }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $env:TEMP ('hz-rtf-' + [guid]::NewGuid().ToString('N'))); RunId = 't1'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$h = New-Fake 'ok'; $r = @(& $module.Run $h.Ctx)
Check ($r.Count -eq 4) ('four cases: ' + $r.Count)
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('self link matches, every link answers, refusal and cleanup pass: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check (($h.State.Deleted -contains 900)) 'the probe link type is deleted'

$h = New-Fake 'differs'; $r = @(& $module.Run $h.Ctx)
Check ($r[1].Outcome -eq 'fail' -and $r[2].Outcome -eq 'pass') 'a self link that differs fails the matches case but still answers'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = @(& $module.Run $h.Ctx)
Check ($r[1].Outcome -eq 'not_covered' -and $r[2].Outcome -eq 'not_covered' -and $r[3].Outcome -eq 'not_covered' -and $r[0].Outcome -eq 'pass') ('write tier closed, no links: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))

if ($fail -gt 0) { Write-Host "$fail check(s) failed"; exit 1 }
Write-Host 'all checks passed'
