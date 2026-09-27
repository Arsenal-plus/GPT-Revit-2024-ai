#Requires -Version 5.1
# Exercises manage-views-renumber.probes.ps1 WITHOUT Revit. The fake keeps a small
# sheet register and answers renumber_sheets the way ManageViewsRenumber.cs builds its
# replies (plan[i].renumber on a rehearsal, rows[i].renumber.renumbered[] with
# reread/verified on an apply, invalid/errors on a refusal) - shapes from the code, to
# be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'manage-views-renumber.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'manage-views-renumber' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$lies = $false) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; sheets = @{}; next = 700; deleted = @() }
    $renumber = {
        param($map, [bool]$write)
        $held = @{}; foreach ($k in $state.sheets.Keys) { $held[$state.sheets[$k]] = $k }
        $moving = @($map.Keys)
        foreach ($old in $map.Keys) {
            if (-not $held.ContainsKey($old)) { return @{ error = "no sheet is numbered '$old'" } }
            $to = $map[$old]
            if ($held.ContainsKey($to) -and $moving -notcontains $to) { return @{ error = "renumber_sheets refused before anything was written: '$to' (target of '$old') is held by a sheet the map does not move." } }
        }
        $cycle = @($map.Keys | Where-Object { $moving -contains $map[$_] }).Count
        $temps = if ($cycle -gt 0) { 1 } else { 0 }
        $steps = @(); foreach ($old in $map.Keys) { $steps += [pscustomobject]@{ sheet_id = [long]$held[$old]; from = $old; to = $map[$old]; temporary = $false } }
        if ($temps) { $steps += [pscustomobject]@{ sheet_id = 0; from = 'x'; to = 'HZTMP-1'; temporary = $true } }
        $renumbered = @()
        if ($write) {
            foreach ($old in $map.Keys) {
                $id = $held[$old]; $state.sheets[$id] = $map[$old]
                $reread = if ($lies) { $old } else { $map[$old] }
                $renumbered += [pscustomobject]@{ sheet_id = [long]$id; from = $old; to = $map[$old]; reread = $reread; verified = -not $lies }
            }
        }
        return @{ plan = [pscustomobject]@{ steps = $steps; temporary_steps = $temps; final = @(); unchanged = @() }; renumbered = $renumbered }
    }.GetNewClosure()
    $call = {
        param($tool, $arguments)
        $a = $arguments.actions[0]
        if ($tool -eq 'horizun_manage_views' -and $a.operation -eq 'renumber_sheets') {
            $r = & $renumber $a.renumber $false
            if ($r.error) { return @{ isError = $false; text = 'rehearsal'; data = [pscustomobject]@{ invalid = 1; errors = @([pscustomobject]@{ index = 0; error = $r.error }); plan = @() } } }
            return @{ isError = $false; data = [pscustomobject]@{ invalid = 0; confirmation_token = 'tok'; plan = @([pscustomobject]@{ index = 0; renumber = $r.plan }) } }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        $a = $arguments.actions[0]
        if ($a.operation -eq 'create_sheet') {
            $aliases = [ordered]@{}
            foreach ($x in $arguments.actions) { $state.next++; $state.sheets[[string]$state.next] = $x.number; $aliases[$x.key] = $state.next }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]$aliases; rows = @() } } }
        }
        if ($a.operation -eq 'renumber_sheets') {
            $r = & $renumber $a.renumber $true
            $row = [pscustomobject]@{ index = 0; operation = 'renumber_sheets'; verified = -not $lies; renumber = [pscustomobject]@{ renumbered = $r.renumbered; temporary_steps = 1 } }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row) } } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected' } }
    }.GetNewClosure()
    return [pscustomobject]@{ Document = 'HZ_WRITE'; RunId = 't1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$failures = 0
function Expect($label, $cond) { if (-not $cond) { Write-Host "FAIL: $label"; $script:failures++ } else { Write-Host "ok: $label" } }

$ctx = New-Ctx $false
$r = @(& $module.Run $ctx)
Expect 'three cases' ($r.Count -eq 3)
Expect 'all pass against a truthful fake' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
$r | Where-Object { $_.Outcome -ne 'pass' } | ForEach-Object { Write-Host ("  " + $_.Outcome + ': ' + $_.Name + ' :: ' + $_.Detail) }
Expect 'the swap landed' ($ctx.State.sheets['701'] -eq 'HZRNt1-2' -and $ctx.State.sheets['702'] -eq 'HZRNt1-1' -and $ctx.State.sheets['703'] -eq 'HZRNt1-4')
Expect 'the three own sheets are deleted' (@($ctx.State.deleted).Count -eq 3)

$ctx = New-Ctx $false $true
$r = @(& $module.Run $ctx)
Expect 'a sheet that re-reads its old number fails the apply case' ($r[1].Outcome -eq 'fail')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' (@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all manage-views-renumber probe tests passed'
