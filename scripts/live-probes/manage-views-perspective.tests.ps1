#Requires -Version 5.1
# Exercises manage-views-perspective.probes.ps1 WITHOUT Revit. The fake answers
# create_perspective the way ManageViewsPerspective.cs builds its replies
# (plan[i].perspective.views_to_create/cameras[] on a rehearsal, rows[i].perspective.views[]
# with reread/orientation_verified/verified on an apply, invalid/errors on a refusal) -
# shapes from the code, to be held against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'manage-views-perspective.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'manage-views-perspective' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$lies = $false) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; next = 900; deleted = @(); views = @{} }
    # PerspectiveRules in miniature: eye/target in mm, azimuth steps of 360/N, fan names by azimuth.
    $cameras = {
        param($a)
        $e = @($a.start); $t = @($a.end)
        $dx = $t[0] - $e[0]; $dy = $t[1] - $e[1]; $dz = $t[2] - $e[2]
        if ([Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz) -lt 1) { return @{ error = 'eye and target coincide: a camera on its own target has no direction.' } }
        $n = if ($a.fan) { [int]$a.fan } else { 1 }
        $h = [Math]::Sqrt($dx * $dx + $dy * $dy); $az0 = [Math]::Atan2($dy, $dx)
        $cams = @()
        for ($k = 0; $k -lt $n; $k++) {
            $az = $az0 + 2 * [Math]::PI * $k / $n
            $deg = [Math]::Round($az * 180 / [Math]::PI, 9); if ($deg -lt 0) { $deg += 360 }; if ($deg -ge 360) { $deg -= 360 }
            $name = if (-not $a.name) { $null } elseif ($n -eq 1) { $a.name } else { $a.name + ' az' + ([int][Math]::Round($deg) % 360).ToString('000') }
            $cams += [pscustomobject]@{ name = $name; azimuth_degrees = $deg; pitch_degrees = [Math]::Round([Math]::Atan2($dz, $h) * 180 / [Math]::PI, 9)
                eye_internal_feet = @(($e[0] / 304.8), ($e[1] / 304.8), ($e[2] / 304.8)); forward = @([Math]::Cos($az), [Math]::Sin($az), 0.0); up = @(0.0, 0.0, 1.0) }
        }
        return @{ cameras = $cams }
    }
    $call = {
        param($tool, $arguments)
        $a = $arguments.actions[0]
        if ($tool -eq 'horizun_manage_views' -and $a.operation -eq 'create_perspective') {
            $r = & $cameras $a
            if ($r.error) { return @{ isError = $false; text = 'rehearsal'; data = [pscustomobject]@{ invalid = 1; errors = @([pscustomobject]@{ index = 0; error = $r.error }); plan = @() } } }
            $pv = [pscustomobject]@{ views_to_create = @($r.cameras).Count; cameras = $r.cameras }
            return @{ isError = $false; data = [pscustomobject]@{ invalid = 0; confirmation_token = 'tok'; plan = @([pscustomobject]@{ index = 0; perspective = $pv }) } }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        if ($tool -eq 'horizun_delete_verified') { $state.deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        $a = $arguments.actions[0]
        if ($a.operation -eq 'create_perspective') {
            $r = & $cameras $a
            if ($r.error) { return @{ stage = 'apply'; answer = @{ isError = $true; text = $r.error } } }
            $views = @()
            foreach ($cm in $r.cameras) {
                $state.next++
                $eye = @($cm.eye_internal_feet)
                # A lying command: the row claims verified while the eye re-read is a foot away.
                if ($lies) { $eye = @(($eye[0] + 1.0), $eye[1], $eye[2]) }
                $views += [pscustomobject]@{ view_id = $state.next; name = $cm.name; azimuth_degrees = $cm.azimuth_degrees; pitch_degrees = $cm.pitch_degrees
                    eye_internal_feet = $cm.eye_internal_feet; forward = $cm.forward; up = $cm.up
                    reread = [pscustomobject]@{ eye_internal_feet = $eye; forward = $cm.forward; up = $cm.up; name = $cm.name; is_perspective = $true }
                    orientation_verified = $true; verified = $true }
                $state.views[[string]$state.next] = $cm.name
            }
            $row = [pscustomobject]@{ index = 0; operation = 'create_perspective'; element_id = $views[0].view_id; verified = $true; perspective = [pscustomobject]@{ views = $views } }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]@{ cam = $views[0].view_id }; rows = @($row) } } }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected' } }
    }.GetNewClosure()
    return [pscustomobject]@{ Document = 'HZ_WRITE'; RunId = 't1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$failures = 0
function Expect($label, $cond) { if (-not $cond) { Write-Host "FAIL: $label"; $script:failures++ } else { Write-Host "ok: $label" } }

$ctx = New-Ctx $false
$r = @(& $module.Run $ctx)
Expect 'four cases' ($r.Count -eq 4)
Expect 'all pass against a truthful fake' (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
$r | Where-Object { $_.Outcome -ne 'pass' } | ForEach-Object { Write-Host ("  " + $_.Outcome + ': ' + $_.Name + ' :: ' + $_.Detail) }
Expect 'the five own views are deleted' (@($ctx.State.deleted).Count -eq 5)
Expect 'the fan names its views by azimuth' (@($ctx.State.views.Values | Where-Object { $_ -like 'HZPVt1 fan az*' }).Count -eq 4)

$ctx = New-Ctx $false $true
$r = @(& $module.Run $ctx)
Expect 'an eye re-read a foot away fails the apply case even when the row claims verified' ($r[1].Outcome -eq 'fail')

$ctx = New-Ctx $true
$r = @(& $module.Run $ctx)
Expect 'closed write tier: all not_covered, nothing applied' (@($r | Where-Object { $_.Outcome -ne 'not_covered' }).Count -eq 0 -and $ctx.State.applies.Count -eq 0)

if ($failures -gt 0) { "$failures failure(s)"; exit 1 }
'all manage-views-perspective probe tests passed'
