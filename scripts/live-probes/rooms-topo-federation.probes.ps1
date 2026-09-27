# Live probe module: rooms in every enclosed circuit, toposolids from points, and the
# federation rule levels_match (see README.md). Loaded by verify-live.ps1; exercised
# without Revit by rooms-topo-federation.tests.ps1.
#
# levels_match: the harness's own link fixture is a scratch COPY of the write document
# linked into itself (the original is never touched), so every level of the link has
# a host twin by name and height: the rule must answer `matches` for that instance.
# The link type is deleted afterwards; the document is never saved.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'rooms-topo-federation'
    Catalog = @(
        @{ Name = 'levels-match: a malformed levels_match is refused';                                   Tool = 'horizun_federation_check' }
        @{ Name = 'levels-match: a copy of the document linked into itself matches every level';        Tool = 'horizun_federation_check' }
        @{ Name = 'levels-match: every link instance answers matches, differs or not_read';             Tool = 'horizun_federation_check' }
        @{ Name = 'levels-match: the probe link is deleted afterwards';                                  Tool = 'horizun_delete_verified' }
        @{ Name = 'rooms all_enclosed: the rehearsal lists both circuits of the own level and phase'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: min_area_m2 = 10 skips the small circuit (skipped_min_area)'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: apply creates two rooms, verified'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: a second call finds every circuit filled and plans nothing'; Tool = 'horizun_create_elements' }
        @{ Name = 'spaces all_enclosed: the rehearsal lists the same circuits'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms all_enclosed: link-bounded circuits are declared not proven'; Tool = 'horizun_create_elements' }
        @{ Name = 'toposolid: six points on an own level, top re-read (2024+) or refused by name (2023)'; Tool = 'horizun_create_elements' }
        @{ Name = 'rooms-topo probes: everything created is deleted'; Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'rooms-topo-federation' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Short($a) { if ($null -eq $a) { return '(no answer)' }; $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        $doc = $Ctx.Document; $run = $Ctx.RunId
        $states = @('matches', 'differs', 'not_read')

        # ---- levels_match: read-only refusal ------------------------------------------------
        $bad = & $Ctx.Call 'horizun_federation_check' @{ target_document = $doc; rules = @{ levels_match = @{ tol = 1 } } }
        Case 0 $(if ($bad.isError -and ([string]$bad.text) -match 'unknown key') { 'pass' } else { 'fail' }) (Short $bad)

        # ---- levels_match against the harness's own link -------------------------------------
        $linkTypeId = $null; $linkInstanceId = $null
        try {
            if (-not $Ctx.WriteGate) {
                $h = & $Ctx.Call 'horizun_health' @{}
                $me = @($h.data.open_documents | Where-Object { $_.title -eq $doc }) | Select-Object -First 1
                if ($me -and $me.path -and (Test-Path -LiteralPath ([string]$me.path))) {
                    New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
                    $src = Join-Path $Ctx.ScratchRoot ('HZ_LVLSRC_' + ($run -replace '[^A-Za-z0-9]', '') + '.rvt')
                    Copy-Item -LiteralPath ([string]$me.path) -Destination $src -Force
                    $add = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; target_document = $doc; path = $src.Replace([char]92, '/') } ($run + '-lm-add')
                    if ($add.stage -eq 'apply' -and -not $add.answer.isError) {
                        $linkTypeId = [long]$add.answer.data.link_type_id
                        $linkInstanceId = [long]$add.answer.data.link_instance_id
                    }
                    else { Case 1 'unverified' ('the probe link could not be added: ' + (Short $add.answer)) }
                }
                else { Case 1 'not_covered' ("the write document's path is not readable from health: " + $me.path) }
            }
            else { Case 1 'not_covered' 'the write tier is closed: the probe cannot link its own copy' }

            $lm = & $Ctx.Call 'horizun_federation_check' @{ target_document = $doc; rules = @{ levels_match = @{ tolerance_mm = 1 }; same_site = $false } }
            if ($lm.isError -or -not $lm.data) {
                if ($linkInstanceId) { Case 1 'fail' ('refused or crashed: ' + (Short $lm)) }
                Case 2 'fail' ('refused or crashed: ' + (Short $lm))
            }
            else {
                $rows = @($lm.data.levels)
                if ($linkInstanceId) {
                    $mine = $rows | Where-Object { [long]$_.instance_id -eq $linkInstanceId } | Select-Object -First 1
                    $ok = $mine -and $mine.state -eq 'matches' -and [int]$mine.levels_compared -gt 0 -and [int]$mine.levels_matching -eq [int]$mine.levels_compared
                    Case 1 $(if ($ok) { 'pass' } else { 'fail' }) ('row: ' + ($mine | ConvertTo-Json -Compress -Depth 5))
                }
                if ($rows.Count -eq 0) { Case 2 'not_covered' 'the document has no link instance to answer for' }
                else {
                    $odd = @($rows | Where-Object { $states -notcontains [string]$_.state })
                    $count = @($lm.data.links).Count
                    $ok = $odd.Count -eq 0 -and $rows.Count -eq $count
                    Case 2 $(if ($ok) { 'pass' } else { 'fail' }) ('{0} rows for {1} links, verdict {2}, summary {3}' -f $rows.Count, $count, $lm.data.verdict, ($lm.data.summary | ConvertTo-Json -Compress))
                }
            }
        }
        catch { Case 2 'unverified' ('probe error: ' + $_) }
        finally {
            if ($linkTypeId) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($linkTypeId); id_cap = 5 } ($run + '-lm-cleanup')
                Case 3 $(if ($del.stage -eq 'apply' -and -not $del.answer.isError) { 'pass' } else { 'fail' }) ('link type ' + $linkTypeId + ': ' + (Short $del.answer))
            }
            else { Case 3 'not_covered' 'no probe link was added' }
        }
        # ---- rooms/spaces in every enclosed circuit, and a toposolid from points ----------
        # Staged far from the model (X = 1,150,000 mm) on levels of its own, so every circuit
        # of the level is one the probe drew: a 6 x 4 m and a 2 x 4 m bay (wall centrelines)
        # sharing a wall - one over and one under min_area_m2 = 10 whichever face Revit
        # measures to. The toposolid's level sits at 500 mm so an absolute and a level-relative
        # reading of Z differ, which is the convention the verification asserts (absolute).
        $CE = 'horizun_create_elements'
        if ($Ctx.WriteGate) {
            for ($i = 4; $i -lt $names.Count; $i++) { Case $i 'not_covered' 'the write tier is closed for this run' }
            return $out.ToArray()
        }
        $created = New-Object System.Collections.Generic.List[long]
        function Rows($r) { if ($r -and $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data) { return @($r.answer.data.rows | Where-Object { $_ -and $_.element_id }) }; return @() }
        function Circ($reply) {
            $b = @($reply.data.enclosed | Where-Object { $_ }) | Select-Object -First 1
            if ($b) { return @($b.circuits | Where-Object { $_ }) }; return @()
        }
        $tplRoot = if ($Ctx.TemplateRoot) { [string]$Ctx.TemplateRoot } else { 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates' }
        $tpl = Join-Path $tplRoot 'English\DefaultMetric.rte'
        function TypeNamed($category, $family, $type) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            return @(@($q.data.rows) | Where-Object { $_ -and $_.is_element_type -and [string]$_.type -eq $type -and [string]$_.family -eq $family }) | Select-Object -First 1
        }
        function Bring($category, $family, $type, $key) {
            $have = TypeNamed $category $family $type
            if ($have -or -not (Test-Path -LiteralPath $tpl)) { return $have }
            $null = & $Ctx.Apply 'horizun_copy_between_documents' @{ target_document = $doc; source_path = $tpl.Replace([char]92, '/'); category = $category
                    type_names = @($family + ': ' + $type); duplicate_types = 'use_destination' } ($run + '-rt-' + $key)
            return TypeNamed $category $family $type
        }
        try {
            $X = 1150000.0; $E = 71000.0
            $wallType = Bring 'OST_Walls' 'Basic Wall' 'Generic - 200mm' 'walltype'
            $lv = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = ('HZ_RT_' + $run); elevation = $E }) } ($run + '-rt-level')))
            $level = if ($lv.Count) { [long]$lv[0].element_id } else { $null }
            if ($level) { $created.Add($level) }
            $walls = @()
            if ($level -and $wallType) {
                $segs = @(@(0, 0, 8000, 0), @(8000, 0, 8000, 4000), @(8000, 4000, 0, 4000), @(0, 4000, 0, 0), @(6000, 0, 6000, 4000))
                $wallRows = @($segs | ForEach-Object { @{ kind = 'wall'; start = @(($X + $_[0]), $_[1], $E); end = @(($X + $_[2]), $_[3], $E); level_id = $level; type_id = [long]$wallType.element_id; height = 3000 } })
                $walls = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = $wallRows } ($run + '-rt-walls')))
                foreach ($w in $walls) { $created.Add([long]$w.element_id) }
            }
            $ph = & $Ctx.Call 'horizun_manage_phases' @{ operation = 'list'; target_document = $doc }
            $phase = @($ph.data.phases | Where-Object { $_ }) | Select-Object -Last 1
            $phaseId = if ($phase) { [long]$phase.id } else { $null }
            if (-not ($level -and $walls.Count -eq 5 -and $phaseId)) {
                $why = "staging incomplete: level {0}, walls {1}/5 ('Basic Wall: Generic - 200mm' {2}), last phase {3}" -f $level, $walls.Count, $(if ($wallType) { 'found' } else { 'not found' }), $phaseId
                for ($i = 4; $i -le 9; $i++) { Case $i 'unverified' $why }
            }
            else {
                $roomEntry = @{ kind = 'room'; placement = 'all_enclosed'; level_id = $level; phase_id = $phaseId }
                $dry = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) }
                $circ = @(Circ $dry)
                $blk = @($dry.data.enclosed | Where-Object { $_ }) | Select-Object -First 1
                Case 4 $(if (-not $dry.isError -and $circ.Count -ge 2 -and [int]$blk.to_create -ge 2) { 'pass' } else { 'fail' }) ('circuits: ' + ($circ | ConvertTo-Json -Compress -Depth 4) + ' ' + (Short $dry))
                $small = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(($roomEntry + @{ min_area_m2 = 10 })) }
                $sc = @(Circ $small)
                $ok = @($sc | Where-Object { $_.action -eq 'skipped_min_area' }).Count -ge 1 -and @($sc | Where-Object { $_.action -eq 'create' }).Count -ge 1
                Case 5 $(if ($ok) { 'pass' } else { 'fail' }) ('actions: ' + (($sc | ForEach-Object { '{0} m2 {1}' -f $_.area_m2, $_.action }) -join ', ') + ' ' + (Short $small))
                $app = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) } ($run + '-rt-rooms')
                $rooms = @(Rows $app)
                foreach ($rm in $rooms) { $created.Add([long]$rm.element_id) }
                Case 6 $(if ($rooms.Count -eq 2) { 'pass' } else { 'fail' }) ('{0} rooms: {1}' -f $rooms.Count, (Short $app.answer))
                $again = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @($roomEntry) }
                $ac = @(Circ $again)
                $ok = -not $again.isError -and [int]$again.data.requested -eq 0 -and $ac.Count -ge 2 -and @($ac | Where-Object { $_.action -ne 'skipped_has_room' }).Count -eq 0
                Case 7 $(if ($ok) { 'pass' } else { 'fail' }) ('requested {0}; actions {1}' -f $again.data.requested, (($ac | ForEach-Object { $_.action }) -join ','))
                $sp = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'space'; placement = 'all_enclosed'; level_id = $level; phase_id = $phaseId }) }
                $spc = @(Circ $sp)
                $at = { param($c) (@($c.point_inside) | ForEach-Object { [math]::Round([double]$_, 0) }) -join ',' }
                $roomPts = @($circ | ForEach-Object { & $at $_ } | Sort-Object); $spacePts = @($spc | ForEach-Object { & $at $_ } | Sort-Object)
                $ok = -not $sp.isError -and $spacePts.Count -ge 2 -and (($roomPts -join ';') -eq ($spacePts -join ';'))
                Case 8 $(if ($ok) { 'pass' } else { 'fail' }) ('rooms ' + ($roomPts -join ';') + ' | spaces ' + ($spacePts -join ';'))
                $lb = [string]$blk.link_bounding
                Case 9 $(if ($lb -match '^not_proven') { 'not_covered' } else { 'fail' }) ('declared by the reply, measured by nobody yet: ' + $lb)
            }

            if ([int]$Ctx.Year -le 2023) {
                $t23 = & $Ctx.Call $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'toposolid'; level_id = $(if ($level) { $level } else { 1 }); type_id = 1; points = @(@(0, 0, 1000), @(1000, 0, 1000), @(0, 1000, 2000)) }) }
                $said = ([string]$t23.text) + ' ' + ($t23.data | ConvertTo-Json -Compress -Depth 6)
                Case 10 $(if ($said -match 'toposolid_not_in_revit_2023') { 'pass' } else { 'fail' }) (Short $t23)
            }
            else {
                $tl = @(Rows (& $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = ('HZ_RT_TOPO_' + $run); elevation = 500 }) } ($run + '-rt-topolevel')))
                $topoLevel = if ($tl.Count) { [long]$tl[0].element_id } else { $null }
                if ($topoLevel) { $created.Add($topoLevel) }
                $topoType = Bring 'OST_Toposolid' 'Toposolid' 'Toposolid' 'topotype'
                if (-not $topoType) {
                    $q = & $Ctx.Call 'horizun_query_model' @{ categories = @('OST_Toposolid'); include_types = $true; include_links = $false; max_rows = 500 }
                    $seen = @(@($q.data.rows) | Where-Object { $_ -and $_.is_element_type } | ForEach-Object { [string]$_.family + ': ' + [string]$_.type })
                    Case 10 'not_covered' ("no toposolid type 'Toposolid: Toposolid' here or in the template; types seen: " + ($seen -join '; '))
                }
                elseif (-not $topoLevel) { Case 10 'unverified' 'the toposolid probe level could not be created' }
                else {
                    $tx = $X + 20000
                    $pts = @(@($tx, 0, 1000), @(($tx + 10000), 0, 1500), @(($tx + 10000), 10000, 4000), @($tx, 10000, 2000), @(($tx + 5000), 5000, 3500), @(($tx + 2000), 3000, 1200))
                    $topo = & $Ctx.Apply $CE @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'toposolid'; level_id = $topoLevel; type_id = [long]$topoType.element_id; points = $pts }) } ($run + '-rt-topo')
                    $made = @(Rows $topo)
                    foreach ($m in $made) { $created.Add([long]$m.element_id) }
                    Case 10 $(if ($made.Count -eq 1) { 'pass' } else { 'fail' }) (Short $topo.answer)
                }
            }
        }
        catch {
            $err = 'probe error: ' + [string]$_
            for ($i = 4; $i -le 10; $i++) { $nm = $names[$i].Name; if (-not @($out | Where-Object { $_.Name -eq $nm }).Count) { Case $i 'unverified' $err } }
        }
        finally {
            if ($created.Count -gt 0) {
                $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created.ToArray()) } ($run + '-rt-cleanup')
                Case 11 $(if ($del.stage -eq 'apply' -and -not $del.answer.isError) { 'pass' } else { 'fail' }) ('{0} ids: {1}' -f $created.Count, (Short $del.answer))
            }
            else { Case 11 'not_covered' 'nothing was created' }
        }
        return $out.ToArray()
    }
}
