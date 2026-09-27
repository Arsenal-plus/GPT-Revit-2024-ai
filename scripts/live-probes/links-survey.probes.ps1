# Live probe module: horizun_manage_links acquire_coordinates, add kind=point_cloud,
# scan_deviation and add kind=ifc (see README.md).
#
# acquire: links a scratch COPY of the write document into itself, MOVES that instance
# 10 m so its site differs from the host's, rehearses acquire_coordinates (the dry run
# is a real rehearsal with rollback: federation_check must still call the link
# incoherent afterwards), places the type a second time and records REVIT's answer for
# the named instance WHILE THE SITES STILL DIFFER (once acquired, the tool's own "already
# shares the host's coordinates" refusal answers first and Revit is never asked), removes
# that placement, applies it (federation_check must now call it coherent), then
# RESTORES the host's shared position with horizun_manage_units base_points from the
# dry run's own project_position_before - whenever the apply RAN, even when it answered
# an error: a committed-but-unverified acquire moved the site as surely as a verified one.
# Never saved.
#
# point cloud: PointCloudPath (a small .rcp/.rcs) from
# %USERPROFILE%\.horizun\live-fixtures.json. Wall and floor types are resolved by family
# (a basic wall, a floor; Generic first), never the document's default: a curtain or
# stacked default would measure panels, not one solid. A wall far from any scan must come back with
# EVERY face not_measured for too_few_points (not for an unreadable cloud or frame); with
# PointCloudFloor (a floor the scan covers, see docs/live-fixtures.example.json) a floor
# staged there must have its top face MEASURED: points >= min_points_per_face, a
# point_frame, ok or deviates. Missing keys: not_covered, named.
#
# IFC: IfcLinkSource when given, otherwise an IFC of the write document exported to
# scratch with horizun_export - so every year is measured. A refusal passes only when it
# is ifc_importer_unavailable AND that year's Revit.IFC.Import.dll is really absent.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'links-survey'
    Catalog = @(
        @{ Name = 'links-survey: acquire_coordinates dry run rehearses for real and rolls back (link still incoherent after it)'; Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: acquire_coordinates apply makes horizun_federation_check call the link coherent';                Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: acquire_coordinates on a type placed twice gets Revit''s own answer for the named instance';      Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: add kind=point_cloud creates a type and an instance that re-read';                              Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: scan_deviation calls every face of a wall the cloud does not reach not_measured (too_few_points)'; Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: scan_deviation measures the top face of a floor staged on a scanned floor';                     Tool = 'horizun_manage_links' }
        @{ Name = 'links-survey: add kind=ifc links a .ifc.RVT with content, or refuses ifc_importer_unavailable only where the importer is absent'; Tool = 'horizun_manage_links' }
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
        if ($Ctx.WriteGate) { foreach ($i in 0..7) { Case $i 'not_covered' 'write tier closed' }; return $out.ToArray() }
        $doc = $Ctx.Document; $run = $Ctx.RunId; $tag = ($run -replace '[^A-Za-z0-9]', '')
        $created = New-Object System.Collections.ArrayList
        $restore = $null; $applyRan = $false
        $fx = $null
        try { $fx = Get-Content -Raw -LiteralPath (Join-Path $env:USERPROFILE '.horizun\live-fixtures.json') | ConvertFrom-Json } catch { $fx = $null }
        function Site($inst) {
            $f = & $Ctx.Call 'horizun_federation_check' @{ rules = @{ same_site = $true } }
            @($f.data.site | Where-Object { [long]$_.instance_id -eq [long]$inst }) | Select-Object -First 1
        }
        function Stage($elements, $key) {
            $r = & $Ctx.Apply 'horizun_create_elements' @{ target_document = $doc; units = 'mm'; elements = $elements } ($run + $key)
            if ($r.stage -ne 'apply' -or $r.answer.isError) { throw ('HZ_STAGE ' + $key + ': ' + (Short $r.answer)) }
            $id = [long]@($r.answer.data.rows)[0].element_id; [void]$created.Insert(0, $id); $id
        }
        function Types($category) {
            $q = & $Ctx.Call 'horizun_query_model' @{ categories = @($category); include_types = $true; include_links = $false; max_rows = 500 }
            if (-not $q.data) { return @() }
            @($q.data.rows | Where-Object { $_.is_element_type })
        }
        # By family, a Generic type first - never the document's default type.
        function WallType { $w = @(Types 'OST_Walls' | Where-Object { -not ($_.family -match 'Curtain|cortina|Stacked|apilad' -or $_.type -match 'Curtain|cortina') }); @(@($w | Where-Object { $_.type -match 'Generic|Gen.rico' }) + $w) | Select-Object -First 1 }
        function FloorType { $fts = @(Types 'OST_Floors' | Where-Object { $_.family -match '^(Floor|Suelo|Piso|Forjado)$' }); @(@($fts | Where-Object { $_.type -match 'Generic|Gen.rico' }) + $fts) | Select-Object -First 1 }
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

            # Placed twice, asked WHILE THE SITE STILL DIFFERS: once acquired, the tool's own "already shares the
            # host's coordinates" refusal answers first and Revit is never asked. The instance is named, so the tool
            # does not pre-refuse the type placed twice: the rehearsal asks Revit, and either answer is Revit's; the
            # tool's own old pre-refusal text would mean a stale add-in.
            $second = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add_instance'; target_document = $doc; link_type_id = $linkType } ($run + '-ls-add2')
            if ($second.stage -ne 'apply' -or $second.answer.isError) { Case 2 'unverified' ('a second placement could not be made: ' + (Short $second.answer)) }
            else {
                $inst2 = [long]$second.answer.data.link_instance_id
                $twice = & $Ctx.Call 'horizun_manage_links' @{ operation = 'acquire_coordinates'; target_document = $doc; link_instance_id = $inst }
                $t = [string]$twice.text
                if ($twice.isError -and $t -match 'multiple times' -and $t -notmatch 'is placed \d+ times') { Case 2 'pass' ('Revit refused the named instance: ' + (Short $twice)) }
                elseif (-not $twice.isError) { Case 2 'pass' ('Revit accepted the named instance of a type placed twice (rehearsed, rolled back): ' + ($twice.data.rehearsal | ConvertTo-Json -Compress -Depth 4)) }
                else { Case 2 'fail' ('not Revit''s answer: ' + (Short $twice)) }
                # The second placement goes before the apply: Revit refuses to acquire from a model placed twice.
                $rm = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($inst2); id_cap = 5 } ($run + '-ls-rm2')
                if ($rm.stage -ne 'apply' -or $rm.answer.isError) { Case 1 'unverified' ('the second placement ' + $inst2 + ' could not be removed before the apply: ' + (Short $rm.answer)); throw 'HZ_STOP' }
            }

            $ap = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'acquire_coordinates'; target_document = $doc; link_instance_id = $inst } ($run + '-ls-acq')
            $applyRan = ($ap.stage -eq 'apply')
            $r = Res $ap.answer.data
            $afterApply = Site $inst
            Case 1 $(if ($applyRan -and -not $ap.answer.isError -and $r.same_site -eq $true -and $afterApply.state -eq 'coherent') { 'pass' } else { 'fail' }) ('apply stage=' + $ap.stage + ' isError=' + $ap.answer.isError + ' same_site=' + $r.same_site + ' delta_after=' + $r.same_site_delta_mm_after + ' federation=' + $afterApply.state + ' ' + (Short $ap.answer))
        }
        catch { if ([string]$_ -ne 'HZ_STOP') { foreach ($i in 0..2) { if (-not (Done $i)) { Case $i 'unverified' ('probe error: ' + $_) } } } }

        # ---- point cloud: add + scan_deviation ---------------------------------------------
        $pcPath = if ($fx) { [string]$fx.PointCloudPath } else { '' }
        if (-not $pcPath -or -not (Test-Path -LiteralPath $pcPath)) {
            foreach ($i in 3..5) { Case $i 'not_covered' ('fixture PointCloudPath (a small .rcp/.rcs) is missing from live-fixtures.json or does not exist: ' + $pcPath) }
        }
        else {
            $pcInst = $null
            try {
                $pa = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; kind = 'point_cloud'; target_document = $doc; path = $pcPath } ($run + '-ls-pc')
                $pr = Res $pa.answer.data
                if ($pa.stage -eq 'apply' -and -not $pa.answer.isError -and $pr.link_type_id) { [void]$created.Add([long]$pr.link_type_id) }
                Case 3 $(if ($pa.stage -eq 'apply' -and -not $pa.answer.isError -and $pr.verified -eq $true) { 'pass' } else { 'fail' }) ('engine=' + $pr.engine + ' found=' + $pr.found_status + ' ' + (Short $pa.answer))
                $pcInst = $pr.link_instance_id
            }
            catch { if (-not (Done 3)) { Case 3 'unverified' ('probe error: ' + $_) } }
            if (-not $pcInst) { foreach ($i in 4..5) { Case $i 'not_covered' 'no point cloud instance to scan' } }
            else {
                # A wall far from any scan (X = 1,170,000 mm, this branch's slot): every face not_measured for too_few_points.
                try {
                    $levelId = Stage @(@{ kind = 'level'; name = "HZ_LS_$tag"; elevation = 0 }) '-ls-level'
                    $wt = WallType
                    if (-not $wt) { Case 4 'not_covered' 'no basic wall type (a wall family other than curtain or stacked) in the write document'; throw 'HZ_SKIP' }
                    $wallId = Stage @(@{ kind = 'wall'; start = @(1170000, 0, 0); end = @(1174000, 0, 0); level_id = $levelId; type_id = [long]$wt.element_id; height = 2500 }) '-ls-wall'
                    $sc = & $Ctx.Call 'horizun_manage_links' @{ operation = 'scan_deviation'; link_instance_id = [long]$pcInst; element_ids = @($wallId); tolerance_mm = 10 }
                    $faces = @($sc.data.elements | ForEach-Object { $_.faces } | Where-Object { $_ })
                    $other = @($faces | Where-Object { $_.state -ne 'not_measured' -or $_.reason -ne 'too_few_points' })
                    Case 4 $(if (-not $sc.isError -and $faces.Count -gt 0 -and $other.Count -eq 0 -and $sc.data.verdict -ne 'passes') { 'pass' } else { 'fail' }) ('verdict=' + $sc.data.verdict + ' faces=' + $faces.Count + ' not too_few_points=' + $other.Count + ' ' + (Short $sc))
                }
                catch { if (-not (Done 4)) { Case 4 'unverified' ('probe error: ' + $_) } }
                # A floor on a floor the scan covers: its top face must be MEASURED.
                $pf = if ($fx) { $fx.PointCloudFloor } else { $null }
                if (-not $pf -or @($pf.min_xy).Count -ne 2 -or @($pf.max_xy).Count -ne 2 -or $null -eq $pf.z) { Case 5 'not_covered' 'fixture PointCloudFloor ({min_xy, max_xy, z} in mm: a floor the scan covers) is missing from live-fixtures.json' }
                else {
                    try {
                        $z = [double]$pf.z; $x0 = [double]$pf.min_xy[0]; $y0 = [double]$pf.min_xy[1]; $x1 = [double]$pf.max_xy[0]; $y1 = [double]$pf.max_xy[1]
                        $fl = Stage @(@{ kind = 'level'; name = "HZ_LSF_$tag"; elevation = $z }) '-ls-flevel'
                        $ft = FloorType
                        if (-not $ft) { Case 5 'not_covered' 'no floor type of the Floor family in the write document'; throw 'HZ_SKIP' }
                        $floorId = Stage @(@{ kind = 'floor'; level_id = $fl; type_id = [long]$ft.element_id; profile = @(,@(@($x0, $y0, $z), @($x1, $y0, $z), @($x1, $y1, $z), @($x0, $y1, $z))) }) '-ls-floor'
                        $sf = & $Ctx.Call 'horizun_manage_links' @{ operation = 'scan_deviation'; link_instance_id = [long]$pcInst; element_ids = @($floorId); tolerance_mm = 10 }
                        $top = @($sf.data.elements | ForEach-Object { $_.faces } | Where-Object { $_ -and @($_.normal).Count -eq 3 -and [double]$_.normal[2] -gt 0.99 }) | Select-Object -First 1
                        $min = [int]$sf.data.min_points_per_face
                        $measured = $top -and [int]$top.points -ge $min -and $min -gt 0 -and @('ok', 'deviates') -contains [string]$top.state -and $top.point_frame
                        Case 5 $(if (-not $sf.isError -and $measured) { 'pass' } else { 'fail' }) ('top face state=' + $top.state + ' reason=' + $top.reason + ' points=' + $top.points + ' p95=' + $top.p95_abs_mm + ' frame=' + $top.point_frame + ' coverage=' + $top.coverage_share + ' ' + (Short $sf))
                    }
                    catch { if (-not (Done 5)) { Case 5 'unverified' ('probe error: ' + $_) } }
                }
            }
        }

        # ---- IFC link ------------------------------------------------------------------------
        try {
            $ifc = Join-Path $Ctx.ScratchRoot ('HZ_IFC_' + $tag + '.ifc')
            $ifcSrc = if ($fx) { [string]$fx.IfcLinkSource } else { '' }
            $how = $null; $ex = $null
            if ($ifcSrc -and (Test-Path -LiteralPath $ifcSrc)) { Copy-Item -LiteralPath $ifcSrc -Destination $ifc -Force; $how = 'fixture IfcLinkSource' }
            else {
                $ex = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'ifc'; output_path = $ifc } ($run + '-ls-ifcexp')
                if ($ex.stage -eq 'apply' -and -not $ex.answer.isError -and (Test-Path -LiteralPath $ifc)) { $how = 'exported from ' + $doc }
            }
            if (-not $how) { Case 6 'unverified' ('no IFC to link: no IfcLinkSource and the export of the write document failed: ' + (Short $ex.answer)) }
            else {
                $ia = & $Ctx.Apply 'horizun_manage_links' @{ operation = 'add'; kind = 'ifc'; target_document = $doc; path = $ifc } ($run + '-ls-ifc')
                $ir = $ia.answer.data
                $revitRoot = if ($Ctx.RevitRoot) { [string]$Ctx.RevitRoot } else { Join-Path $env:ProgramFiles 'Autodesk' }
                $importerDll = Join-Path $revitRoot ('Revit ' + $Ctx.Year + '\Revit.IFC.Import.dll')
                if ($ia.stage -eq 'apply' -and -not $ia.answer.isError -and $ir.verified -eq $true -and [int]$ir.linked_direct_shapes -gt 0) {
                    [void]$created.Add([long]$ir.link_type_id)
                    Case 6 'pass' ('Revit ' + $Ctx.Year + ' linked ' + $ir.intermediate_rvt + ' (' + $ir.linked_direct_shapes + ' DirectShapes) from ' + $how)
                }
                elseif ($ia.stage -eq 'apply' -and ([string]$ia.answer.text) -match 'ifc_importer_unavailable') {
                    if (Test-Path -LiteralPath $importerDll) { Case 6 'fail' ('refused ifc_importer_unavailable although ' + $importerDll + ' exists: ' + (Short $ia.answer)) }
                    else { Case 6 'pass' ('Revit ' + $Ctx.Year + ' has no importer (' + $importerDll + ' absent) and refused by name') }
                }
                else {
                    if ($ir.link_type_id) { [void]$created.Add([long]$ir.link_type_id) }
                    Case 6 'fail' ('stage=' + $ia.stage + ' from ' + $how + ': ' + (Short $ia.answer))
                }
            }
        }
        catch { if (-not (Done 6)) { Case 6 'unverified' ('probe error: ' + $_) } }

        # ---- restore + cleanup -------------------------------------------------------------------
        $problems = @()
        if ($applyRan -and $restore) {
            $pp = @{ east_west = [double]$restore.east_west; north_south = [double]$restore.north_south; elevation = [double]$restore.elevation; angle_to_true_north = [double]$restore.angle_to_true_north }
            $rs = & $Ctx.Apply 'horizun_manage_units' @{ operation = 'base_points'; target_document = $doc; units = 'mm'; project_position = $pp; confirm_shared_coordinates = $true } ($run + '-ls-restore')
            if ($rs.stage -ne 'apply' -or $rs.answer.isError) { $problems += ('shared coordinates NOT restored: ' + (Short $rs.answer)) }
        }
        elseif ($applyRan) { $problems += 'the acquire apply ran but the before-position was not published; shared coordinates NOT restored' }
        $ids = @($created.ToArray())
        if ($ids.Count -gt 0) {
            $del = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $ids; id_cap = 50 } ($run + '-ls-cleanup')
            if ($del.stage -ne 'apply' -or $del.answer.isError) { $problems += ('left in the disposable document: ' + ($ids -join ',') + ' - ' + (Short $del.answer)) }
        }
        if ($ids.Count -eq 0 -and -not $applyRan) { Case 7 'not_covered' 'nothing was staged' }
        elseif ($problems.Count -eq 0) { Case 7 'pass' ('restored=' + [bool]$applyRan + ' deleted ' + ($ids -join ',')) }
        else { Case 7 'fail' ($problems -join ' | ') }
        # Recorded out of order (the placed-twice case runs before the apply): returned in catalog order.
        $order = @($names | ForEach-Object { $_.Name })
        return @($out.ToArray() | Sort-Object { [array]::IndexOf($order, $_.Name) })
    }
}
