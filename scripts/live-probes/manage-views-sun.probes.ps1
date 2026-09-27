# Live probes for horizun_manage_views operation=set_sun_study (a view's sun: still,
# single_day or multi_day, instants with their offset, optional site lat/lon).
# The probe stages its OWN 3D view (named with this run's id), sets its sun, and
# deletes the view at the end; it never saves. The location case moves the PROJECT
# site (the only writable place for lat/lon) and puts the coordinates it read in
# the rehearsal back afterwards - Revit re-derives the place name and time zone
# from them both times, so on a fixture whose time zone was typed by hand that one
# value may not come back: the probe reports it, and the document is never saved.
#
# Reply shapes (plan[i].sun.requested/current/location_scope, rows[i].sun with
# before/reread/checks/verified, invalid/errors on a refused rehearsal) are from the
# code (ManageViewsSunStudy.cs / ManageViewsCommand.cs), to be held against the
# first live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'manage-views-sun'
    Catalog = @(
        @{ Name = 'sun: the rehearsal shows the current and requested sun, scope unchanged, and binds a token'; Tool = 'horizun_manage_views' }
        @{ Name = 'sun: a single-day study applies and re-reads its type and both instants in UTC'; Tool = 'horizun_manage_views' }
        @{ Name = 'sun: a still sun with lat/lon discloses project scope, re-reads the site angles and is put back'; Tool = 'horizun_manage_views' }
        @{ Name = 'sun: an instant without an offset is refused before writing'; Tool = 'horizun_manage_views' }
        @{ Name = 'sun: an end on a still sun is refused before writing'; Tool = 'horizun_manage_views' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $V = 'horizun_manage_views'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        function Utc($s) { [DateTimeOffset]::Parse([string]$s, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
        function Near($a, $b) { [Math]::Abs(((Utc $a) - (Utc $b)).TotalSeconds) -le 60 }
        $doc = $Ctx.Document
        $names = @('sun: the rehearsal shows the current and requested sun, scope unchanged, and binds a token',
                   'sun: a single-day study applies and re-reads its type and both instants in UTC',
                   'sun: a still sun with lat/lon discloses project scope, re-reads the site angles and is put back',
                   'sun: an instant without an offset is refused before writing',
                   'sun: an end on a still sun is refused before writing')
        function AllNotCovered($why) { foreach ($n in $names) { Case $n $V 'not_covered' $why } }

        if ($Ctx.WriteGate) { AllNotCovered 'write tier is not open for this run'; return $cases.ToArray() }

        $tag = 'HZSUN' + $Ctx.RunId
        $st = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'create_3d'; name = "$tag view" }) } 'sun-stage'
        $viewId = if (Applied $st) { [long](@($st.answer.data.rows)[0].element_id) } else { 0 }
        if ($viewId -le 0) { AllNotCovered ('could not stage an own 3D view: ' + (Why $st)); return $cases.ToArray() }

        $day = @{ operation = 'set_sun_study'; view_id = $viewId; sun = @{ type = 'single_day'; start = '2026-06-21T07:00:00-05:00'; end = '2026-06-21T18:00:00-05:00' } }
        $req = @{ target_document = $doc; actions = @($day) }
        $dry = & $Ctx.Call $V ($req + @{ dry_run = $true })
        $pv = if ($dry.data) { @($dry.data.plan)[0].sun } else { $null }
        $site = if ($pv -and $pv.current) { $pv.current } else { $null }
        if ($pv -and $pv.requested.type -eq 'OneDayStudy' -and $pv.requested.start_utc -eq '2026-06-21T12:00:00Z' -and
            $pv.requested.end_utc -eq '2026-06-21T23:00:00Z' -and $pv.location_scope -eq 'unchanged' -and $site -and $dry.data.confirmation_token) {
            Case $names[0] $V 'pass' ('current=' + ($site | ConvertTo-Json -Compress -Depth 3))
        } else { Case $names[0] $V 'fail' ('plan=' + ($pv | ConvertTo-Json -Compress -Depth 5) + ' text=' + [string]$dry.text) }

        $ap = & $Ctx.Apply $V $req 'sun-day'
        if (Applied $ap) {
            $row = @($ap.answer.data.rows) | Select-Object -First 1
            $rr = $row.sun.reread
            # Independent of the command's own flag: the instants read back are the ones asked for.
            if ($row.verified -eq $true -and $row.sun.verified -eq $true -and $rr.type -eq 'OneDayStudy' -and
                (Near $rr.start_utc '2026-06-21T12:00:00Z') -and (Near $rr.end_utc '2026-06-21T23:00:00Z')) {
                Case $names[1] $V 'pass' ('re-read: ' + ($rr | ConvertTo-Json -Compress -Depth 3))
            } else { Case $names[1] $V 'fail' ('row=' + ($row | ConvertTo-Json -Compress -Depth 6)) }
        } else { Case $names[1] $V 'fail' (Why $ap) }

        if ($null -eq $site -or $null -eq $site.site_latitude_degrees -or $null -eq $site.site_longitude_degrees) {
            Case $names[2] $V 'not_covered' 'the rehearsal did not read the site coordinates, so they could not be put back'
        } else {
            $lat0 = [double]$site.site_latitude_degrees; $lon0 = [double]$site.site_longitude_degrees
            $still = @{ operation = 'set_sun_study'; view_id = $viewId; sun = @{ type = 'still'; start = '2026-12-21T12:00:00+00:00'; lat = 10.0; lon = -20.0 } }
            $sreq = @{ target_document = $doc; actions = @($still) }
            $sd = & $Ctx.Call $V ($sreq + @{ dry_run = $true })
            $spv = if ($sd.data) { @($sd.data.plan)[0].sun } else { $null }
            $as = & $Ctx.Apply $V $sreq 'sun-site'
            $back = & $Ctx.Apply $V @{ target_document = $doc; actions = @(@{ operation = 'set_sun_study'; view_id = $viewId;
                        sun = @{ type = 'still'; start = '2026-12-21T12:00:00+00:00'; lat = $lat0; lon = $lon0 } }) } 'sun-site-back'
            $restored = (Applied $back) -and (@($back.answer.data.rows)[0].sun.verified -eq $true)
            if (Applied $as) {
                $row = @($as.answer.data.rows) | Select-Object -First 1
                $rr = $row.sun.reread
                $angles = $rr -and [Math]::Abs([double]$rr.site_latitude_degrees - 10.0) -lt 1e-5 -and [Math]::Abs([double]$rr.site_longitude_degrees + 20.0) -lt 1e-5
                if ($spv -and $spv.location_scope -eq 'project_site' -and @($spv.location_side_effects).Count -ge 1 -and
                    $row.verified -eq $true -and $row.sun.location_scope -eq 'project_site' -and $rr.type -eq 'StillImage' -and $angles -and $restored) {
                    Case $names[2] $V 'pass' ('site re-read ' + $rr.site_latitude_degrees + ',' + $rr.site_longitude_degrees + '; put back to ' + $lat0 + ',' + $lon0)
                } else { Case $names[2] $V 'fail' ('restored=' + $restored + ' plan=' + ($spv | ConvertTo-Json -Compress -Depth 4) + ' row=' + ($row | ConvertTo-Json -Compress -Depth 6)) }
            } else { Case $names[2] $V 'fail' ((Why $as) + ' restored=' + $restored) }
        }

        $bad = @{ operation = 'set_sun_study'; view_id = $viewId; sun = @{ type = 'still'; start = '2026-06-21T12:00:00' } }
        $rf = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @($bad) }
        $said = [string]$rf.text + ' ' + ($rf.data.errors | ConvertTo-Json -Compress -Depth 4)
        if (($rf.isError -or ($rf.data -and [int]$rf.data.invalid -eq 1)) -and $said -match 'offset' -and -not $rf.data.confirmation_token) {
            Case $names[3] $V 'pass' ('refused: ' + $said.Substring(0, [Math]::Min(240, $said.Length)))
        } else { Case $names[3] $V 'fail' ('not refused by name: ' + $said) }

        $bad = @{ operation = 'set_sun_study'; view_id = $viewId; sun = @{ type = 'still'; start = '2026-06-21T12:00:00Z'; end = '2026-06-21T13:00:00Z' } }
        $rf = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @($bad) }
        $said = [string]$rf.text + ' ' + ($rf.data.errors | ConvertTo-Json -Compress -Depth 4)
        if (($rf.isError -or ($rf.data -and [int]$rf.data.invalid -eq 1)) -and $said -match 'ONE instant' -and -not $rf.data.confirmation_token) {
            Case $names[4] $V 'pass' ('refused: ' + $said.Substring(0, [Math]::Min(240, $said.Length)))
        } else { Case $names[4] $V 'fail' ('not refused by name: ' + $said) }

        $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($viewId) } 'sun-cleanup'
        return $cases.ToArray()
    }
}
