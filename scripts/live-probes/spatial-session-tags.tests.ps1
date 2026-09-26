#Requires -Version 5.1
# Exercises spatial-session-tags.probes.ps1 WITHOUT Revit: a fake Call/Apply plays
# two writes (a duplicate pair + a harmless wall), a base-offset edit, and two
# overlapping text notes.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'spatial-session-tags.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'spatial-session-tags' }
$fail = 0
function Check($ok, $what) { if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:fail++ } }

function New-Fake([string]$mode) {
    $s = @{ Mode = $mode; Next = 100; Deleted = @(); WallAIds = @(); TextIds = @() }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_query_model' {
                if ($a.categories[0] -eq 'OST_TextNotes') {
                    if ($s.Mode -eq 'no-texttype') { return & $reply ([pscustomobject]@{ rows = @() }) }
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 55; is_element_type = $true; family = 'Text Note'; type = 'Default' }) })
                }
                if ($s.Mode -eq 'no-walltype') { return & $reply ([pscustomobject]@{ rows = @() }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 7; is_element_type = $true; family = 'Basic Wall'; type = 'Generic' }) })
            }
            'horizun_query_planimetry' {
                if ($s.Mode -eq 'no-plan') { return & $reply ([pscustomobject]@{ rows = @() }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 900; view_type = 'FloorPlan'; is_template = $false }) })
            }
            'horizun_verify_changes' {
                if ($a.include_annotation -eq $true) {
                    $findings = if ($s.Mode -eq 'no-explicit-overlap') { @() } else {
                        @([pscustomobject]@{ a = [pscustomobject]@{ id = $s.TextIds[0] }; b = [pscustomobject]@{ id = $s.TextIds[1] }; reason = 'two annotations overlap' })
                    }
                    return & $reply ([pscustomobject]@{ scope = [pscustomobject]@{ source = 'last_write' }; spatial_check = [pscustomobject]@{ status = 'clean'; findings = @() }
                        annotation_check = [pscustomobject]@{ status = 'overlaps'; findings = $findings } })
                }
                if ($a.scope -eq 'session') {
                    if ($s.Mode -eq 'session-blind') {
                        return & $reply ([pscustomobject]@{ scope = [pscustomobject]@{ source = 'session'; writes_considered = 2 }; spatial_check = [pscustomobject]@{ status = 'clean'; findings = @() } })
                    }
                    $f = [pscustomobject]@{ kind = 'duplicate'; severity = 'error'; a = [pscustomobject]@{ id = $s.WallAIds[0] }; b = [pscustomobject]@{ id = $s.WallAIds[1] }; reason = 'duplicate' }
                    return & $reply ([pscustomobject]@{ scope = [pscustomobject]@{ source = 'session'; writes_considered = 2 }; spatial_check = [pscustomobject]@{ status = 'conflicts'; findings = @($f) } })
                }
                # default (scope=last_write): only sees the far wall from write B - clean.
                return & $reply ([pscustomobject]@{ scope = [pscustomobject]@{ source = 'last_write' }; spatial_check = [pscustomobject]@{ status = 'clean'; findings = @() } })
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        if ($tool -eq 'horizun_delete_verified') { $s.Deleted = @($a.ids); return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
        if ($tool -eq 'horizun_create_elements') {
            $ids = @()
            foreach ($el in $a.elements) { $ids += $s.Next; $s.Next++ }
            if ($key -like '*-dup') { $s.WallAIds = $ids }
            $rows = @($ids | ForEach-Object { [pscustomobject]@{ element_id = $_ } })
            return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = $rows })) }
        }
        if ($tool -eq 'horizun_write_params_verified') {
            if ($s.Mode -eq 'data-only-skipped') { return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true })) } }
            $sc = [pscustomobject]@{ status = 'clean'; scope = 'data-only write: 1 element(s) whose bounding box moved' }
            return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ ok = $true; spatial_check = $sc })) }
        }
        if ($tool -eq 'horizun_annotate') {
            $id1 = $s.Next; $s.Next++; $id2 = $s.Next; $s.Next++
            $s.TextIds = @($id1, $id2)
            $rows = @([pscustomobject]@{ element_id = $id1 }, [pscustomobject]@{ element_id = $id2 })
            $findings = if ($s.Mode -eq 'no-auto-overlap') { @() } else {
                @([pscustomobject]@{ a = [pscustomobject]@{ id = $id1 }; b = [pscustomobject]@{ id = $id2 }; reason = 'two text notes overlap' })
            }
            $ac = [pscustomobject]@{ status = $(if ($findings.Count -gt 0) { 'overlaps' } else { 'clean' }); findings = $findings }
            return @{ stage = 'apply'; answer = (& $reply ([pscustomobject]@{ rows = $rows; annotation_check = $ac })) }
        }
        return @{ stage = 'apply'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't'; WriteGate = $false; Call = $call; Apply = $apply } }
}
function Outcomes($h) { @(& $module.Run $h.Ctx) }

$h = New-Fake 'ok'; $r = Outcomes $h
Check ($r.Count -eq 4) 'four cases, one per catalog entry'
Check (@($r | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0) ('everything passes: ' + (($r | ForEach-Object { $_.Outcome }) -join ','))
Check ($h.State.Deleted.Count -eq 7) 'level, two duplicate walls, the far wall, the offset wall and both text notes are deleted'

$h = New-Fake 'session-blind'; $r = Outcomes $h
Check ($r[0].Outcome -eq 'fail') 'scope=session that does NOT surface the earlier duplicate fails the session case'

$h = New-Fake 'data-only-skipped'; $r = Outcomes $h
Check ($r[1].Outcome -eq 'fail') 'a data-only write with no spatial_check at all fails the base-offset case'

$h = New-Fake 'no-auto-overlap'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'fail') 'no automatic overlap finding on horizun_annotate''s own reply fails the tag-overlap case'

$h = New-Fake 'no-explicit-overlap'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'fail') 'no overlap finding from the explicit include_annotation call fails the tag-overlap case'

$h = New-Fake 'no-walltype'; $r = Outcomes $h
Check (@($r[0..2] | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 3) 'a fixture without a basic wall type is not_covered on all three measured cases, never a pass'
Check ($h.State.Deleted.Count -eq 1) 'the staged level is still deleted'

$h = New-Fake 'no-plan'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'not_covered') 'a fixture without a non-template floor plan is not_covered on the tag-overlap case only'
Check ($r[0].Outcome -eq 'pass' -and $r[1].Outcome -eq 'pass') 'the session and base-offset cases are unaffected by a missing plan view'

$h = New-Fake 'no-texttype'; $r = Outcomes $h
Check ($r[2].Outcome -eq 'not_covered') 'a fixture without a text note type is not_covered on the tag-overlap case only'

$h = New-Fake 'ok'; $h.Ctx.WriteGate = $true; $r = Outcomes $h
Check (@($r | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 4) 'with the write tier closed every case is not_covered'

if ($fail -gt 0) { Write-Host "spatial-session-tags probe tests: $fail FAILED"; exit 1 }
Write-Host 'spatial-session-tags probe tests: ALL PASS'
