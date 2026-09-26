#Requires -Version 5.1
# Exercises sleeves-openings.probes.ps1 WITHOUT Revit: a fake Call/Apply plays a model
# with an own wall, floor and beam crossed by own pipes, a ledger and the two
# opening operations of horizun_resolve_clash.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'sleeves-openings.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'sleeves-openings' }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Ids = @{}; Ledger = [ordered]@{}; Calls = New-Object System.Collections.ArrayList
            Deleted = @(); BeamApplied = $false; Rehearsals = 0; Structural = $false; MissingStructural = $false }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $pairs = @(
        @{ Fid = 'fw'; Pipe = 'pipe-wall';  Host = 'wall';  Kind = 'wall';  Route = 'wall_opening' }
        @{ Fid = 'ff'; Pipe = 'pipe-floor'; Host = 'floor'; Kind = 'floor'; Route = 'floor_opening' }
        @{ Fid = 'fb'; Pipe = 'pipe-beam';  Host = 'beam';  Kind = 'framing_or_column'; Route = 'sleeve_only' })
    $call = {
        param($tool, $a)
        [void]$s.Calls.Add($tool)
        switch ($tool) {
            'horizun_query_model' {
                if ($s.Mode -eq 'no-framing' -and @($a.categories) -contains 'OST_StructuralFraming') { return & $reply ([pscustomobject]@{ rows = @() }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) })
            }
            'horizun_clash' {
                foreach ($p in $pairs) {
                    if ($s.Ids.ContainsKey($p.Pipe) -and -not $s.Ledger.Contains($p.Fid)) { $s.Ledger[$p.Fid] = @{ status = 'open'; history = @(@{ kind = 'opened' }) } }
                }
                return & $reply ([pscustomobject]@{ findings = [pscustomobject]@{ new = $s.Ledger.Count } })
            }
            'horizun_coordination' {
                # The REAL update (MEASURED 2026-09-26): no token, applied at once and re-read;
                # an explicit dry_run=true rehearses and writes nothing.
                if ($a.operation -eq 'update') {
                    [void]$s.Calls.Add('coordination-update')
                    if ($a.dry_run -eq $true) { return & $reply ([pscustomobject]@{ dry_run = $true; would_leave = [pscustomobject]@{ status = $a.status } }) }
                    $before = $s.Ledger[[string]$a.finding_id].status
                    $s.Ledger[[string]$a.finding_id].status = [string]$a.status
                    return & $reply ([pscustomobject]@{ status_before = $before; verified_after_reread = $true; row = [pscustomobject]@{ finding_id = $a.finding_id; status = $a.status } })
                }
                $rows = @($s.Ledger.Keys | ForEach-Object { [pscustomobject]@{ finding_id = $_; status = $s.Ledger[$_].status
                    history = @($s.Ledger[$_].history | ForEach-Object { [pscustomobject]$_ }) } })
                return & $reply ([pscustomobject]@{ rows = $rows })
            }
            'horizun_resolve_clash' {
                if ($a.allow_structural -eq $true) { $s.Structural = $true } else { $s.MissingStructural = $true }
                if ($a.operation -eq 'propose_opening') {
                    $rows = @(); $acts = @()
                    foreach ($p in $pairs) {
                        if (@($a.finding_ids) -notcontains $p.Fid) { continue }
                        $row = [pscustomobject]@{ finding_id = $p.Fid; status = 'proposed'; mep_element_id = $s.Ids[$p.Pipe]; host_element_id = $s.Ids[$p.Host]
                            host_kind = $p.Kind; route = $p.Route; crossing_point_mm = @(1, 2, 3); opening_width_mm = 214; opening_height_mm = 214; shape = 'round'; cut_refused = $null }
                        if ($p.Kind -eq 'framing_or_column' -and $s.Mode -ne 'beam-cut-accepted') { $row.cut_refused = [pscustomobject]@{ code = 'member_cut_not_offered' } }
                        $rows += $row
                        $acts += [pscustomobject]@{ finding_id = $p.Fid; mep_element_id = $row.mep_element_id; host_element_id = $row.host_element_id; host_kind = $p.Kind; route = $p.Route }
                    }
                    return & $reply ([pscustomobject]@{ read_only = $true; proposals = $rows; next_arguments = [pscustomobject]@{ proposals = $acts } })
                }
                if ($a.operation -eq 'apply_opening' -and $a.dry_run -eq $true) {
                    $s.Rehearsals++
                    $act = @($a.proposals)[0]
                    $errs = @()
                    if ($act.host_kind -eq 'framing_or_column' -and $s.Mode -ne 'beam-cut-accepted') { $errs += [pscustomobject]@{ finding_id = $act.finding_id; code = 'member_cut_not_offered' } }
                    $tok = if ($errs.Count -eq 0) { 'tok' } else { $null }
                    return & $reply ([pscustomobject]@{ dry_run = $true; errors = $errs; confirmation_token = $tok })
                }
            }
        }
        return & $reply $null $true 'unexpected call'
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        [void]$s.Calls.Add($tool + ':apply')
        switch ($tool) {
            'horizun_create_elements' {
                $id = $s.Next; $s.Next++
                $s.Ids[($key -replace '^.*-so-', '')] = $id
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) }
            }
            'horizun_resolve_clash' {
                if ($a.allow_structural -eq $true) { $s.Structural = $true } else { $s.MissingStructural = $true }
                $act = @($a.proposals)[0]
                if ($act.host_kind -eq 'framing_or_column') { $s.BeamApplied = $true; return @{ stage = 'dry_run'; answer = (& $reply $null $true 'refused') } }
                if ($s.Mode -eq 'apply-fails') { return @{ stage = 'apply'; answer = (& $reply $null $true 'Rolled back, nothing kept: a postcondition failed') } }
                $fid = [string]$act.finding_id
                $s.Ledger[$fid].history += @{ kind = 'opening_requested' }
                $oid = $s.Next; $s.Next++
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{
                    postconditions = [pscustomobject]@{ all_verified = $true }; verdict = 'fake verdict'
                    created = @([pscustomobject]@{ finding_id = $fid; created_element_id = $oid; kind = 'opening'; placement = 'native_opening'; host_kind = $act.host_kind; host_cut = $true })
                    findings_opening_requested = @($fid); findings_resolved_by_model = @() })) }
            }
            'horizun_delete_verified' { $s.Deleted = @($a.ids); return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
            'horizun_coordination' {
                # update is not a token operation: a rehearse-then-apply wrapper finds no token.
                [void]$s.Calls.Add('coordination-apply-wrapper')
                return @{ stage = 'dry_run'; answer = (& $reply ([pscustomobject]@{ dry_run = $true })) }
            }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true 'unexpected apply') }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Outcome($cases, $i) { (@($cases | Where-Object { $_.Name -eq $module.Catalog[$i].Name }) | Select-Object -First 1).Outcome }

$f = New-Fake 'ok'
$cases = @(& $module.Run $f.Ctx)
Check 'every catalogued case is reported once' ($cases.Count -eq $module.Catalog.Count)
Check 'names match the catalog exactly' (@($cases | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
Check 'all seven pass on a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the beam is never sent to apply, only rehearsed' ((-not $f.State.BeamApplied) -and $f.State.Rehearsals -eq 1)
Check 'the cleanup closes exactly the probe''s own findings' ((@($f.State.Ledger.Keys | Where-Object { $f.State.Ledger[$_].status -eq 'closed_by_decision' }).Count -eq 3))
Check 'every propose and apply carries the fixture''s structural approval' ($f.State.Structural -and -not $f.State.MissingStructural)
Check 'the cleanup deletes the openings too, and the level last' ((@($f.State.Deleted).Count -eq 9) -and (@($f.State.Deleted)[-1] -eq $f.State.Ids['level']))
Check 'the probe''s findings are closed by one tokenless update each, re-read, never through the rehearse-then-apply wrapper' ((@($f.State.Ledger.Values | Where-Object { $_.status -ne 'closed_by_decision' }).Count -eq 0) -and (@($f.State.Calls | Where-Object { $_ -eq 'coordination-update' }).Count -eq $f.State.Ledger.Count) -and -not ($f.State.Calls -contains 'coordination-apply-wrapper'))

$g = New-Fake 'no-framing'
$cases2 = @(& $module.Run $g.Ctx)
Check 'without a framing type the beam case is not_covered and the rest still pass' (((Outcome $cases2 5) -eq 'not_covered') -and
    (@($cases2 | Where-Object { $_.Outcome -eq 'pass' }).Count -eq 6))

$h = New-Fake 'apply-fails'
$cases3 = @(& $module.Run $h.Ctx)
Check 'a refused apply fails the cut cases, leaves the ledger case unverified and still cleans up' (((Outcome $cases3 1) -eq 'fail') -and
    ((Outcome $cases3 2) -eq 'unverified') -and ((Outcome $cases3 4) -eq 'fail') -and ($h.State.Calls -contains 'horizun_delete_verified:apply'))

$k = New-Fake 'beam-cut-accepted'
$cases4 = @(& $module.Run $k.Ctx)
Check 'a beam cut that is not refused by name fails the refusal case' ((Outcome $cases4 5) -eq 'fail')

$w = New-Fake 'ok'; $w.Ctx.WriteGate = $true
$cases5 = @(& $module.Run $w.Ctx)
Check 'with the write tier closed every case is not_covered and nothing is called' ((@($cases5 | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 7) -and ($w.State.Calls.Count -eq 0))

if ($fails) { "sleeves-openings tests: $fails FAILED"; exit 1 } else { 'sleeves-openings tests: ALL PASS'; exit 0 }
