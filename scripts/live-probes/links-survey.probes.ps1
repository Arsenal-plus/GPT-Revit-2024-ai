# Live probe module: horizun_manage_links acquire_coordinates, add kind=point_cloud,
# scan_deviation and add kind=ifc (see README.md).
#
# acquire: links a scratch COPY of the write document into itself, MOVES that instance
# 10 m so its site differs from the host's, rehearses acquire_coordinates (the dry run
# is a real rehearsal with rollback: federation_check must still call the link
# incoherent afterwards), applies it (federation_check must now call it coherent),
# proves a second placement is refused by name, then RESTORES the host's shared
# position with horizun_manage_units base_points from the dry run's own
# project_position_before. Never saved.
#
# point cloud / IFC need files this repository does not ship. They are read from
# %USERPROFILE%\.horizun\live-fixtures.json, keys PointCloudPath (a small .rcp/.rcs)
# and IfcLinkSource (a disposable .ifc, copied to scratch before linking so the
# .ifc.RVT lands there). Missing keys make those cases not_covered, named.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'links-survey'
    Catalog = @(
        @{ Name = 'links-survey: acquire_coordinates dry run rehearses for real and rolls back (link still incoherent after it)'; Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: acquire_coordinates apply makes horizun_federation_check call the link coherent';                Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: acquire_coordinates refuses a link type placed twice, by name';                                  Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: add kind=point_cloud creates a type and an instance that re-read';                              Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: scan_deviation never reports ok for faces the cloud does not reach';                            Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: add kind=ifc links the .ifc.RVT or refuses ifc_importer_unavailable by name';                   Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: shared coordinates restored and everything staged deleted';                                     Tool = 'horizun_delete_verified' }
    )
    Run     = {
        param($Ctx)
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'links-survey' } | Select-Object -First 1).Catalog
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $outcome, $detail) { $out.Add(@{ Name = $names[$i].Name; Tool = $names[$i].Tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Done($i) { @($out | Where-Object { $_.Name -eq $names[$i].Name }).Count -gt 0 }
        function Short($a) { $t = [string]$a.text; if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t } }
        function Res($d) { if ($d.result) { $d.result } else { $d } }   # VerifiedModelEdit publishes edit.Result under result
        # $Ctx.WriteGate TRUE means the write tier is CLOSED.
        if ($Ctx.WriteGate) { foreach ($i in 0..6) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId; $tag = ($run -replace '[^A-Za-z0-9]', '')
        $created = New-Object System.Collections.ArrayList
        $restore = $null; $acquired = $false
        $fx = $null
        try { $fx = Get-Content -Raw -LiteralPath (Join-Path $env:USERPROFILE '.horizun\live-fixtures.json') | ConvertFrom-Json } catch { $fx = $null }
        function Site($inst) {
            $f = & $Ctx.Call 'horizun_federation_check' @{ rules = @{ same_site = $true } }
            @($f.data.site | Where-Object { [long]$_.instance_id -eq [long]$inst }) | Select-Object -First 1
        }
        New-Item -ItemType Directory -Force -Path $Ctx.ScratchRoot | Out-Null
        # ---- acquire_coordinates ------------------------------------------------------
        try {
            $h = & $Ctx.Call 'horizun_health' @{}
            $me = @($h.data.open_documents | Where-Object { $_.title -eq $doc }) | Select-Object -First 1
            if (-not $me -or -not $me.path -or -not (Test-Path -LiteralPath ([string]$me.path))) { foreach ($i in 0..2) { Case $i 'not_covered' ("the write document's path is not readable from health: " + $me.path) }; throw 'HZ_STOP' }
            $src = Join-Path $Ctx.ScratchRoot ('HZ_ACQ_' + $tag + '.rvt')
            Copy-Item -LiteralPath ([string]$me.path) -Destination $src -Force
            $add = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; target_document = $doc; path = $src.Replace([char]92, '/') } ($run + '-ls-add')
            if ($add.stage -ne 'apply' -or $add.answer.isError) { foreach ($i in 0..2) { Case $i 'unverified' ('the link could not be added: ' + (Short $add.answer)) }; throw 'HZ_STOP' }
            $linkType = [long]$add.answer.data.link_type_id; $inst = [long]$add.answer.data.link_instance_id
            [void]$created.Add($linkType)
            $mv = & $Ctx.Apply 'horizun_transform_elements' @{ target_document = $doc; units = 'mm'; operations = @(@{ operation = 'move'; element_ids = @($inst); vector = @(10000, 0, 0) }) } ($run + '-ls-move')
            if ($mv.stage -ne 'apply' -or $mv.answer.isError) { foreach ($i in 0..2) { Case $i 'unverified' ('the link instance could not be moved off-site: ' + (Short $mv.answer)) }; throw 'HZ_STOP' }

            $dry = & $Ctx.Call 'horizun_manage_links' @{ operation = 'acquire_coordinates'; target_document = $doc; link_instance_id = $inst }
            $restore = $dry.data.plan.project_position_before
            $afterDry = Site $inst
            Case 0 $(if (-not $dry.isError -and $restore -and $afterDry.state -eq 'incoherent') { 'pass' } else { 'fail' }) ('dry isError=' + $dry.isError + ' rehearsal=' + ($dry.data.rehearsal | ConvertTo-Json -Compress -Depth 4) + ' site after dry run=' + $afterDry.state + ' delta=' + $afterDry.max_delta_mm)

            $ap = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'acquire_coordinates'; target_document = $doc; link_instance_id = $inst } ($run + '-ls-acq')
            $acquired = ($ap.stage -eq 'apply' -and -not $ap.answer.isError)
            $r = Res $ap.answer.data
            $afterApply = Site $inst
            Case 1 $(if ($acquired -and $r.same_site -eq $true -and $afterApply.state -eq 'coherent') { 'pass' } else { 'fail' }) ('apply stage=' + $ap.stage + ' same_site=' + $r.same_site + ' delta_after=' + $r.same_site_delta_mm_after + ' federation=' + $afterApply.state + ' ' + (Short $ap.answer))

            $second = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add_instance'; target_document = $doc; link_type_id = $linkType } ($run + '-ls-add2')
            if ($second.stage -ne 'apply' -or $second.answer.isError) { Case 2 'unverified' ('a second placement could not be made: ' + (Short $second.answer)) }
            else {
                $twice = & $Ctx.Call 'horizun_manage_links' @{ operation = 'acquire_coordinates'; target_document = $doc; link_instance_id = $inst }
                Case 2 $(if ($twice.isError -and ([string]$twice.text) -match 'placed 2 times') { 'pass' } else { 'fail' }) (Short $twice)
            }
        }
        catch { if ([string]$_ -ne 'HZ_STOP') { foreach ($i in 0..2) { if (-not (Done $i)) { Case $i 'unverified' ('probe error: ' + $_) } } } }

        # ---- point cloud: add + scan_deviation ---------------------------------------------
        $pcPath = if ($fx) { [string]$fx.PointCloudPath } else { '' }
        if (-not $pcPath -or -not (Test-Path -LiteralPath $pcPath)) {
            foreach ($i in 3..4) { Case $i 'not_covered' ('fixture PointCloudPath (a small .rcp/.rcs) is missing from live-fixtures.json or does not exist: ' + $pcPath) }
        }
        else {
            try {
                $pa = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; kind = 'point_cloud'; target_document = $doc; path = $pcPath } ($run + '-ls-pc')
                $pr = Res $pa.answer.data
                if ($pa.stage -eq 'apply' -and -not $pa.answer.isError -and $pr.link_type_id) { [void]$created.Add([long]$pr.link_type_id) }
                Case 3 $(if ($pa.stage -eq 'apply' -and -not $pa.answer.isError -and $pr.verified -eq $true) { 'pass' } else { 'fail' }) ('engine=' + $pr.engine + ' found=' + $pr.found_status + ' ' + (Short $pa.answer))
                if (-not $pr.link_instance_id) { Case 4 'not_covered' 'no point cloud instance to scan'; throw 'HZ_STOP' }
                # A wall far from any scan (X = 1,170,000 mm, this branch's slot): every face must come back not_measured.
                $lv = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'level'; name = "HZ_LS_$tag"; elevation = 0 }) } ($run + '-ls-level')
                $levelId = if ($lv.stage -eq 'apply' -and -not $lv.answer.isError) { [long]@($lv.answer.data.rows)[0].element_id } else { $null }
                if ($levelId) { [void]$created.Insert(0, $levelId) }
                $w = if ($levelId) { & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = @(@{ kind = 'wall'; start = @(1170000, 0, 0); end = @(1174000, 0, 0); level_id = $levelId; height = 2500 }) } ($run + '-ls-wall') } else { $null }
                if (-not $w -or $w.stage -ne 'apply' -or $w.answer.isError) { Case 4 'unverified' ('the staged wall could not be created: ' + (Short $w.answer)); throw 'HZ_STOP' }
                $wallId = [long]@($w.answer.data.rows)[0].element_id; [void]$created.Insert(0, $wallId)
                $sc = & $Ctx.Call 'horizun_manage_links' @{ operation = 'scan_deviation'; link_instance_id = [long]$pr.link_instance_id; element_ids = @($wallId); tolerance_mm = 10 }
                $faces = @($sc.data.elements | ForEach-Object { $_.faces } | Where-Object { $_ })
                $okFaces = @($faces | Where-Object { $_.state -eq 'ok' })
                Case 4 $(if (-not $sc.isError -and $faces.Count -gt 0 -and $okFaces.Count -eq 0 -and $sc.data.verdict -ne 'passes') { 'pass' } else { 'fail' }) ('verdict=' + $sc.data.verdict + ' faces=' + $faces.Count + ' ok=' + $okFaces.Count + ' ' + (Short $sc))
            }
            catch { if ([string]$_ -ne 'HZ_STOP') { foreach ($i in 3..4) { if (-not (Done $i)) { Case $i 'unverified' ('probe error: ' + $_) } } } }
        }

        # ---- IFC link ------------------------------------------------------------------------
        $ifcSrc = if ($fx) { [string]$fx.IfcLinkSource } else { '' }
        if (-not $ifcSrc -or -not (Test-Path -LiteralPath $ifcSrc)) { Case 5 'not_covered' ('fixture IfcLinkSource (a disposable .ifc) is missing from live-fixtures.json or does not exist: ' + $ifcSrc) }
        else {
            try {
                $ifc = Join-Path $Ctx.ScratchRoot ('HZ_IFC_' + $tag + '.ifc'); Copy-Item -LiteralPath $ifcSrc -Destination $ifc -Force
                $ia = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; kind = 'ifc'; target_document = $doc; path = $ifc } ($run + '-ls-ifc')
                if ($ia.stage -eq 'apply' -and -not $ia.answer.isError -and $ia.answer.data.verified -eq $true) {
                    [void]$created.Add([long]$ia.answer.data.link_type_id)
                    Case 5 'pass' ('Revit ' + $Ctx.Year + ' linked ' + $ia.answer.data.intermediate_rvt + ' by ' + $ia.answer.data.linked_by)
                }
                elseif ($ia.stage -eq 'apply' -and ([string]$ia.answer.text) -match 'ifc_importer_unavailable') { Case 5 'pass' ('Revit ' + $Ctx.Year + ' refused by name: ' + (Short $ia.answer)) }
                else { Case 5 'fail' ('stage=' + $ia.stage + ' ' + (Short $ia.answer)) }
            }
            catch { Case 5 'unverified' ('probe error: ' + $_) }
        }

        # ---- restore + cleanup -------------------------------------------------------------------
        $problems = @()
        if ($acquired -and $restore) {
            $pp = @{ east_west = [double]$restore.east_west; north_south = [double]$restore.north_south; elevation = [double]$restore.elevation; angle_to_true_north = [double]$restore.angle_to_true_north }
            $rs = & $Ctx.Apply 'horizun_manage_units' @{ operation = 'base_points'; target_document = $doc; units = 'mm'; project_position = $pp; confirm_shared_coordinates = $true } ($run + '-ls-restore')
            if ($rs.stage -ne 'apply' -or $rs.answer.isError) { $problems += ('shared coordinates NOT restored: ' + (Short $rs.answer)) }
        }
        elseif ($acquired) { $problems += 'shared coordinates acquired but the before-position was not published; NOT restored' }
        $ids = @($created.ToArray())
        if ($ids.Count -gt 0) {
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 50 } ($run + '-ls-cleanup')
            if ($del.stage -ne 'apply' -or $del.answer.isError) { $problems += ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        if ($ids.Count -eq 0 -and -not $acquired) { Case 6 'not_covered' 'nothing was staged' }
        elseif ($problems.Count -eq 0) { Case 6 'pass' ('restored=' + [bool]$acquired + ' deleted ' + ($ids -join ',')) }
        else { Case 6 'fail' ($problems -join ' | ') }
        return $out.ToArray()
    }
}
