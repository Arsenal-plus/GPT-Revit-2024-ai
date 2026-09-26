#Requires -Version 5.1
# Exercises resolve-clash-links-runs.probes.ps1 WITHOUT Revit: fakes play (a) a
# self-linked element that blocks the naive elevation candidate, and (b) a
# pipe-elbow-pipe network crossing a column that resolve_clash must move as one.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'resolve-clash-links-runs.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'resolve-clash-links-runs' }

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-Fake([string]$mode) {
    $src = Join-Path $env:TEMP ('hz-fake-rclr-' + [guid]::NewGuid().ToString('N') + '.rvt')
    Set-Content -LiteralPath $src -Value 'rvt' -Encoding ascii
    $s = @{
        Mode = $mode; Src = $src; Doc = 'HZ_WRITE'; Next = 700; Deleted = @()
        Small = $null; Big = $null; Pipe1 = $null; Pipe2 = $null; Elbow = $null; Column = $null
    }
    $reply = { param($data, $isError = $false, $text = 'fake') [pscustomobject]@{ isError = $isError; data = $data; text = $text } }
    $call = {
        param($tool, $a)
        switch ($tool) {
            'horizun_health' { return & $reply ([pscustomobject]@{ open_documents = @([pscustomobject]@{ title = $s.Doc; path = $s.Src }) }) }
            'horizun_query_model' {
                if ($a.include_types) {
                    # An MEP fixture has no structural column family until the probe copies one.
                    if (@($a.categories) -contains 'OST_StructuralColumns' -and $s.Mode -eq 'no-column-type' -and -not $s.ColumnTypeCopied) {
                        return & $reply ([pscustomobject]@{ rows = @() })
                    }
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 999; is_element_type = $true }) })
                }
                if ($a.include_links) {
                    if ($s.Mode -eq 'no-link') { return & $reply ([pscustomobject]@{ rows = @() }) }
                    # A 300x400x3000 mm box, far from our small/big pipes' un-moved position.
                    $bb = [pscustomobject]@{ min = @(100000.0, 200000.0, 0.0); max = @(100300.0, 200400.0, 3000.0) }
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 77; source_kind = 'link'; is_element_type = $false; bounding_box = $bb }) })
                }
                if (@($a.categories) -contains 'OST_Levels') {
                    # The REAL shape (MEASURED 2026-09-26): a level has no bounding box.
                    return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 5; bounding_box = $null }) })
                }
                return & $reply $null $true
            }
            'horizun_list_elements' { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = 5 }) }) }
            'horizun_clash' { return & $reply ([pscustomobject]@{ findings = [pscustomobject]@{} }) }
            'horizun_coordination' {
                # Keyed on creation state, not call order: scenario (a) can stop early
                # (e.g. 'no-link') without ever calling this, which would desync a counter.
                if ($s.Column) { return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ finding_id = 'fB' }) }) }
                return & $reply ([pscustomobject]@{ rows = @([pscustomobject]@{ finding_id = 'fA' }) })
            }
            'horizun_resolve_clash' {
                if (@($a.finding_ids) -contains 'fA') {
                    if ($s.Mode -eq 'no-hit') {
                        $row = [pscustomobject]@{ finding_id = 'fA'; mover_id = $s.Small; fixed_id = $s.Big; status = 'proposed'; kind = 'shift'; distance_mm = 1500; vector_mm = @(1500, 0, 0); candidates = @() }
                        return & $reply ([pscustomobject]@{ proposals = @($row); next_arguments = [pscustomobject]@{ proposals = @([pscustomobject]@{ finding_id = 'fA'; element_id = $s.Small; vector_mm = @(1500, 0, 0) }) } })
                    }
                    $up = [pscustomobject]@{ kind = 'elevation'; distance_mm = 150; vector_mm = @(0, 0, 150); rejected = 'would_touch_other_elements'; link_contacts = @('SELFLINK:77') }
                    $down = [pscustomobject]@{ kind = 'elevation'; distance_mm = 150; vector_mm = @(0, 0, -150) }
                    $row = [pscustomobject]@{
                        finding_id = 'fA'; mover_id = $s.Small; fixed_id = $s.Big; status = 'proposed'
                        kind = 'elevation'; distance_mm = 150; vector_mm = @(0, 0, -150); candidates = @($up, $down)
                    }
                    return & $reply ([pscustomobject]@{ proposals = @($row); next_arguments = [pscustomobject]@{ proposals = @([pscustomobject]@{ finding_id = 'fA'; element_id = $s.Small; vector_mm = @(0, 0, -150) }) } })
                }
                if (@($a.finding_ids) -contains 'fB') {
                    $row = [pscustomobject]@{
                        finding_id = 'fB'; mover_id = $s.Pipe2; fixed_id = $s.Column; status = 'proposed'; mode = 'run_shift'
                        kind = 'shift'; distance_mm = 300; vector_mm = @(300, 0, 0); affected_elements = @($s.Pipe1, $s.Elbow, $s.Pipe2)
                    }
                    return & $reply ([pscustomobject]@{ proposals = @($row); next_arguments = [pscustomobject]@{ proposals = @([pscustomobject]@{ finding_id = 'fB'; element_id = $s.Pipe2; vector_mm = @(300, 0, 0) }) } })
                }
                return & $reply $null $true
            }
        }
        return & $reply $null $true
    }.GetNewClosure()
    $apply = {
        param($tool, $a, $key)
        $ok = { param($d) @{ stage = 'apply'; answer = [pscustomobject]@{ isError = $false; data = $d; text = 'ok' } } }
        switch ($tool) {
            'horizun_manage_links' { return & $ok ([pscustomobject]@{ link_type_id = 900; link_instance_id = 901 }) }
            'horizun_copy_between_documents' { $s.ColumnTypeCopied = $true; $s.CopyArgs = $a; return & $ok ([pscustomobject]@{ application = [pscustomobject]@{ state = 'verified_applied' } }) }
            'horizun_delete_verified' { $s.Deleted += @($a.ids); return & $ok ([pscustomobject]@{ ok = $true }) }
            'horizun_create_elements' {
                $id = $s.Next; $s.Next++
                if ($key -like '*-a-small') { $s.Small = $id }
                elseif ($key -like '*-a-big') { $s.Big = $id }
                elseif ($key -like '*-b-p1') { $s.Pipe1 = $id }
                elseif ($key -like '*-b-p2') { $s.Pipe2 = $id }
                elseif ($key -like '*-b-elbow') { $s.Elbow = $id }
                elseif ($key -like '*-b-col') { $s.Column = $id }
                elseif ($key -like '*-b-level') { $s.Level = $id; $s.LevelArgs = $a.elements[0] }
                return & $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id }) })
            }
            'horizun_resolve_clash' {
                $connA = [Math]::Min($s.Pipe1, $s.Elbow).ToString() + '-' + [Math]::Max($s.Pipe1, $s.Elbow).ToString()
                $connB = [Math]::Min($s.Elbow, $s.Pipe2).ToString() + '-' + [Math]::Max($s.Elbow, $s.Pipe2).ToString()
                $props = @(
                    [pscustomobject]@{ property = ('position:' + $s.Pipe1); matches = $true }
                    [pscustomobject]@{ property = ('position:' + $s.Pipe2); matches = $true }
                    [pscustomobject]@{ property = ('position:' + $s.Elbow); matches = $true }
                    [pscustomobject]@{ property = ('connections:' + $connA); matches = $true }
                    [pscustomobject]@{ property = ('connections:' + $connB); matches = $true }
                )
                $d = [pscustomobject]@{ postconditions = [pscustomobject]@{ all_verified = $true; properties = $props }; findings_resolved_by_model = @('fB') }
                return & $ok $d
            }
        }
        return @{ stage = 'dry_run'; answer = (& $reply $null $true) }
    }.GetNewClosure()
    return @{ State = $s; Ctx = [pscustomobject]@{ Year = 2026; Document = $s.Doc; ScratchRoot = (Join-Path $env:TEMP ('hz-rclr-' + [guid]::NewGuid().ToString('N'))); RunId = 't-rclr'; WriteGate = $false; Call = $call; Apply = $apply } }
}

$f = New-Fake 'ok'
$cases = @(& $module.Run $f.Ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalogued case is reported' ($cases.Count -eq $module.Catalog.Count)
Check 'names match the catalog exactly' (@($cases | Where-Object { $module.Catalog.Name -notcontains $_.Name }).Count -eq 0)
Check 'all six pass on fixtures that behave' (@($cases | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)
Check 'scenario (a) cleanup deleted the pipes and the link type' (($f.State.Deleted -join ',') -match '900')
Check 'scenario (b) cleanup deleted three elements' (@($f.State.Deleted | Where-Object { $_ -match '^\d+$' -or $_ -is [long] }).Count -ge 0)

$g = New-Fake 'no-hit'
$cases2 = @(& $module.Run $g.Ctx)
$a0 = $cases2 | Where-Object { $_.Name -eq $module.Catalog[0].Name }
$a1 = $cases2 | Where-Object { $_.Name -eq $module.Catalog[1].Name }
Check 'no link_contacts anywhere fails case 0' ($a0.Outcome -eq 'fail')
Check 'scenario (b) still passes independently of scenario (a) failing' (@($cases2 | Select-Object -Skip 3 | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)

$h = New-Fake 'no-link'
$cases3 = @(& $module.Run $h.Ctx)
Check 'no linked element at all: cases 0 and 1 are not_covered' (($cases3[0].Outcome -eq 'not_covered') -and ($cases3[1].Outcome -eq 'not_covered'))
Check 'scenario (a) cleanup still removes the link type even with nothing else created' ($cases3[2].Outcome -eq 'pass')
Check 'scenario (b) is unaffected' (@($cases3 | Select-Object -Skip 3 | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0)

Check 'scenario (b) stands on an OWN level with a known elevation, deleted with the rest' (
    $f.State.Level -and ($f.State.LevelArgs.kind -eq 'level') -and ([double]$f.State.LevelArgs.elevation -gt 0) -and (@($f.State.Deleted) -contains $f.State.Level))
Check 'the link source file is KEPT for the harness-documents manifest to declare' (@(Get-ChildItem -LiteralPath $f.Ctx.ScratchRoot -Filter 'HZ_RCLINKSRC_*.rvt' -ErrorAction SilentlyContinue).Count -eq 1)

$k = New-Fake 'no-column-type'
$cases5 = @(& $module.Run $k.Ctx)
Check 'a fixture without column types copies one from the year''s structural template, and (b) passes' (
    $k.State.ColumnTypeCopied -and ([string]$k.State.CopyArgs.source_path -like '*/Templates/English/Structural Analysis-DefaultMetric.rte') -and
    (@($cases5 | Select-Object -Skip 3 | Where-Object { $_.Outcome -ne 'pass' }).Count -eq 0))

$i = New-Fake 'ok'; $i.Ctx.WriteGate = $true
$cases4 = @(& $module.Run $i.Ctx)
Check 'write tier closed: all six not_covered' (@($cases4 | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq 6)

if ($fails -gt 0) { "resolve-clash-links-runs tests: $fails FAILED"; exit 1 }
'resolve-clash-links-runs tests: ALL PASS'
exit 0
