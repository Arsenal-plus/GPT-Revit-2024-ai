#Requires -Version 5.1
# Exercises manage-views-sun.probes.ps1 WITHOUT Revit. The fake answers set_sun_study
# the way ManageViewsSunStudy.cs builds its replies (plan[i].sun.requested/current/
# location_scope on a rehearsal, rows[i].sun with before/reread/checks/verified on an
# apply, invalid/errors on a refusal) - shapes from the code, to be held against the
# first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'manage-views-sun.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'manage-views-sun' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$lies = $false) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; next = 700; deleted = @(); lat = 51.5; lon = -0.12
                type = 'StillImage'; start = '2026-01-01T12:00:00Z'; end = '2026-01-01T13:00:00Z' }
    $iso = { param($s) [DateTimeOffset]::Parse([string]$s, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }
    # SunStudyRules in miniature: the offset is required, a still sun refuses an end.
    $parse = {
        param($sun)
        if ([string]$sun.start -notmatch '(Z|[+-]\d{2}:\d{2})$') { return @{ error = 'sun.start must be an ISO-8601 date-time with its offset' } }
        if ($sun.type -eq 'still' -and $sun.end) { return @{ error = 'a still sun takes ONE instant (start); sun.end would be ignored by Revit' } }
        $type = @{ still = 'StillImage'; single_day = 'OneDayStudy'; multi_day = 'MultiDayStudy' }[[string]$sun.type]
        return @{ type = $type; start = (& $iso $sun.start); end = $(if ($sun.end) { & $iso $sun.end } else { $null }); lat = $sun.lat; lon = $sun.lon }
    }.GetNewClosure()
    $current = { [pscustomobject]@{ settings_id = 5; type = $state.type; start_utc = $state.start; end_utc = $state.end; shares_settings = $false
                                    site_latitude_degrees = $state.lat; site_longitude_degrees = $state.lon } }.GetNewClosure()
    $call = {
        param($tool, $arguments)
        $a = $arguments.actions[0]
        if ($tool -eq 'horizun_manage_views' -and $a.operation -eq 'set_sun_study') {
            $r = & $parse $a.sun
            if ($r.error) { return @{ isError = $false; text = 'rehearsal'; data = [pscustomobject]@{ invalid = 1; errors = @([pscustomobject]@{ index = 0; error = $r.error }); plan = @() } } }
            $moves = $null -ne $r.lat
            $pv = [pscustomobject]@{ requested = [pscustomobject]@{ type = $r.type; start_utc = $r.start; end_utc = $r.end; site_latitude_degrees = $r.lat; site_longitude_degrees = $r.lon }
                                     location_scope = $(if ($moves) { 'project_site' } else { 'unchanged' }); current = (& $current) }
            if ($moves) { $pv | Add-Member location_side_effects @("every view's sun moves", 'Revit re-derives place name, time zone and weather station') }
            return @{ isError = $false; data = [pscustomobject]@{ invalid = 0; confirmation_token = 'tok'; plan = @([pscustomobject]@{ index = 0; sun = $pv }) } }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        $a = $arguments.actions[0]
        if ($a.operation -eq 'create_3d') {
            $state.next++
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ index = 0; operation = 'create_3d'; element_id = $state.next; verified = $true }) } } }
        }
        if ($a.operation -eq 'set_sun_study') {
            $r = & $parse $a.sun
            if ($r.error) { return @{ stage = 'apply'; answer = @{ isError = $true; text = $r.error } } }
            $before = & $current
            $state.type = $r.type; $state.start = $r.start; if ($r.end) { $state.end = $r.end }
            if ($null -ne $r.lat) { $state.lat = [double]$r.lat; $state.lon = [double]$r.lon }
            $reread = & $current
            # A lying command: the row claims verified while the start re-read is an hour off.
            if ($lies) { $reread.start_utc = ([DateTime]::Parse($state.start, [Globalization.CultureInfo]::InvariantCulture, 'AdjustToUniversal').AddHours(1)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }
            $sun = [pscustomobject]@{ view_id = $a.view_id; before = $before; sunrise_to_sunset_cleared = $false
                                      location_scope = $(if ($null -ne $r.lat) { 'project_site' } else { 'unchanged' }); reread = $reread
                                      checks = [pscustomobject]@{ type = $true; start = $true; end = $true; site = $true }; verified = $true }
            $row = [pscustomobject]@{ index = 0; operation = 'set_sun_study'; element_id = $a.view_id; verified = $true; sun = $sun }
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
Expect 'five cases' ($r.Count -eq 5)
Expect 'all pass against a truthful fake' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
$r | Where-Object { $_.Outcome -ne 'pass' } | ForEach-Object { Write-Host ("  " + $_.Outcome + ': ' + $_.Name + ' :: ' + $_.Detail) }
Expect 'the own view is deleted' (@($ctx.State.deleted).Count -eq 1 -and [long]$ctx.State.deleted[0] -eq 701)
Expect 'the site is put back to what the rehearsal read' ($ctx.State.lat -eq 51.5 -and $ctx.State.lon -eq -0.12)

$ctx = New-Ctx $false $true
$r = @(& $module.Run $ctx)
Expect 'a start re-read an hour off fails the study case even when the row claims verified' ($r[1].Outcome -eq 'fail')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' (@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all manage-views-sun probe tests passed'
