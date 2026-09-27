#Requires -Version 5.1
# Exercises coordination-bcf-readiness.probes.ps1 WITHOUT Revit: a fake Call/Apply
# plays a model with a level/pipe type, a written-then-read-back IfcGUID, a
# synthetic BCF import that reproduces and records one finding, a navisworks_readiness
# verdict, and a prepare_navisworks dry_run+apply on a disposable view. The module's
# OWN zip-building (Compress-Archive over real temp files) runs for real even here.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'coordination-bcf-readiness.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'coordination-bcf-readiness' }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 200; ViewNext = 900; Recorded = $false; FindingId = 'f-bcf-1'; Calls = @() }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        $s.Calls += $tool
        switch ($tool) {
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 1 }) }) }
            'horizun_query_model' {
                if ($a.element_ids) {
                    # the read-back after writing IfcGUID
                    $rows = @($a.element_ids | ForEach-Object {
                            [pscustomobject]@{ element_id = $_; parameters = [pscustomobject]@{ IfcGUID = ('HZPROBE' + 't' + $(if ($_ -eq $a.element_ids[0]) { 'SMALL' } else { 'BIG' })) } }
                        })
                    return & $reply ([pscustomobject]@{ rows = $rows })
                }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true }) })
            }
            'horizun_coordination' {
                switch ($a.operation) {
                    'import' {
                        if ($a.dry_run -eq $false) {
                            $s.Recorded = $true
                            return & $reply ([pscustomobject]@{ external_recorded = 1; verified_by_reread = $true })
                        }
                        if ($s.Mode -eq 'no-ifcguid') { return & $reply ([pscustomobject]@{ external_reproduced = @(); external_would_record = 0 }) }
                        $row = [pscustomobject]@{ topic_guid = 'HZBCF-t'; finding_id = $s.FindingId }
                        return & $reply ([pscustomobject]@{ external_reproduced = @($row); external_would_record = 1 })
                    }
                    'list' {
                        if (-not $s.Recorded) { return & $reply ([pscustomobject]@{ rows = @() }) }
                        $row = [pscustomobject]@{ finding_id = $s.FindingId; external_issue_id = 'HZBCF-t'; external_source = 'bcf' }
                        return & $reply ([pscustomobject]@{ rows = @($row) })
                    }
                    'navisworks_readiness' {
                        # The REAL refusal (MEASURED 2026-09-26, Revit 2023 HZ23_BASE) until the probe
                        # has created its own 'Navisworks' view; then that view is the verdict's.
                        if ($s.Mode -eq 'no-view' -and -not $s.LastViewId) {
                            return & $reply $null $true "Error: No 3D view whose name contains 'Navisworks', and no default '{3D}' view either - those are what Navisworks' own Revit reader looks for. Create one, or point Navisworks at a different view directly. Nothing was read."
                        }
                        if ($s.Mode -eq 'no-view') { return & $reply ([pscustomobject]@{ detail_level = 'Coarse'; verdict = 'not_ready'; view_name = 'Navisworks'; view_id = $s.LastViewId }) }
                        if ($s.Mode -eq 'refused-other') { return & $reply $null $true 'Error: target_document does not match the active document.' }
                        $viewName = if ($s.Mode -eq 'real-navisworks-view') { 'Navisworks' } else { '{3D}' }
                        return & $reply ([pscustomobject]@{ detail_level = 'Coarse'; verdict = 'not_ready'; view_name = $viewName; view_id = 42 })
                    }
                    'prepare_navisworks' {
                        if ($a.dry_run -ne $false) {
                            return & $reply ([pscustomobject]@{ confirmation_token = 'tok-1'; view_id = $s.LastViewId; detail_level_after = 'Fine' })
                        }
                        return & $reply $null $true
                    }
                }
                return & $reply $null $true
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $s.Calls += ($tool + ':apply:' + ($a.operation))
        switch ($tool) {
            'horizun_create_elements' { $id = $s.Next; $s.Next++; return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })) } }
            'horizun_write_params_verified' {
                if ($s.Mode -eq 'no-ifcguid') { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ verification = [pscustomobject]@{ verified = $false } })) } }
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ verification = [pscustomobject]@{ verified = $true } })) }
            }
            'horizun_manage_views' {
                $id = $s.ViewNext; $s.ViewNext++; $s.LastViewId = $id
                return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ aliases = [pscustomobject]@{ nwprobe = $id } })) }
            }
            'horizun_coordination' {
                if ($a.operation -eq 'prepare_navisworks') {
                    return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ detail_level = 'Fine'; postconditions = [pscustomobject]@{ all_verified = $true } })) }
                }
                return @{ stage = 'apply'; answer = (& $reply $null $true) }
            }
            'horizun_delete_verified' { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    $scratch = Join-Path $env:TEMP ('hz-bcfr-tests-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$f = New-Fake 'ok'
$cases = @(& $module.Run $f.Ctx)
Check 'every catalogued case is reported' ($cases.Count -eq $module.Catalog.Count)
Check 'names match the catalog exactly' (@($cases | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
Check 'all six pass on a model that behaves' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'the disposable view was created before prepare_navisworks ran' ($f.State.Calls.IndexOf('horizun_manage_views:apply:') -lt $f.State.Calls.IndexOf('horizun_coordination:apply:prepare_navisworks'))
Check 'the pipes and the disposable view are deleted at the end' ($f.State.Calls -contains 'horizun_delete_verified:apply:')

$g = New-Fake 'no-ifcguid'
$cases2 = @(& $module.Run $g.Ctx)
Check 'no writable IfcGUID parameter: cases 0-1 are not_covered, the rest still run' (
    $cases2[0].Outcome -eq 'not_covered' -and $cases2[1].Outcome -eq 'not_covered' -and $cases2[2].Outcome -eq 'pass' -and $cases2[4].Outcome -eq 'pass')
Check 'a failed IfcGUID write still reports every catalogued case' ($cases2.Count -eq $module.Catalog.Count)
Check 'the pipes are still deleted after a failed IfcGUID write' ($g.State.Calls -contains 'horizun_delete_verified:apply:')

$h = New-Fake 'no-view'
$cases3 = @(& $module.Run $h.Ctx)
Check 'no Navisworks/{3D} view: the refusal is recorded, the probe stages its own view and readiness passes on it' (
    $cases3[2].Outcome -eq 'pass' -and ([string]$cases3[2].Detail -match 'first call refused') -and ([string]$cases3[2].Detail -match '"view_name":"Navisworks"'))
Check 'with its own view, prepare dry run and apply run on that view' ($cases3[3].Outcome -eq 'pass' -and $cases3[4].Outcome -eq 'pass')
Check 'exactly one disposable view is created' (@($h.State.Calls | Where-Object { $_ -eq 'horizun_manage_views:apply:' }).Count -eq 1)
Check 'every catalogued case is reported, cleanup included (no return inside the try)' (
    $cases3.Count -eq $module.Catalog.Count -and @($cases3 | Where-Object { $_.Name -eq $module.Catalog[5].Name -and $_.Outcome -eq 'pass' }).Count -eq 1)

$r = New-Fake 'refused-other'
$cases6 = @(& $module.Run $r.Ctx)
Check 'any other readiness refusal fails case 2, leaves 3-4 not_covered and creates no view' (
    $cases6[2].Outcome -eq 'fail' -and $cases6[3].Outcome -eq 'not_covered' -and $cases6[4].Outcome -eq 'not_covered' -and
    -not ($r.State.Calls -contains 'horizun_manage_views:apply:'))
Check 'the cleanup case is still reported after an early stop' (@($cases6 | Where-Object { $_.Name -eq $module.Catalog[5].Name }).Count -eq 1)

$k = New-Fake 'real-navisworks-view'
$cases4 = @(& $module.Run $k.Ctx)
Check 'a real Navisworks view already exists: readiness passes but prepare is not_covered, never touched' (
    $cases4[2].Outcome -eq 'pass' -and $cases4[3].Outcome -eq 'not_covered' -and $cases4[4].Outcome -eq 'not_covered')
Check 'a pre-existing Navisworks view is never created or deleted by this probe' (-not ($k.State.Calls -contains 'horizun_manage_views:apply:'))

$w = New-Fake 'ok'; $w.Ctx.WriteGate = $true
$cases5 = @(& $module.Run $w.Ctx)
Check 'without the write gate every case is not_covered' (@($cases5 | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 6)

if ($fails) { "coordination-bcf-readiness tests: $fails FAILED"; exit 1 } else { 'coordination-bcf-readiness tests: ALL PASS'; exit 0 }
