# Live probes for horizun_manage_views operation=create_perspective (a camera from
# eye=start, target=end and an optional up; fan=N cameras turned about world Z).
# A camera needs no staged element: the probe creates its OWN perspective views
# looking along X = 1,120,000 mm (this branch's slot), names them with this run's
# id, never saves, and deletes every view it made at the end.
#
# Reply shapes (plan[i].perspective.views_to_create/cameras[], rows[i].perspective.views[]
# with reread/orientation_verified/verified, invalid/errors on a refused rehearsal) are
# from the code (ManageViewsPerspective.cs / ManageViewsCommand.cs), to be held against
# the first live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'manage-views-perspective'
    Catalog = @(
        @{ Name = 'perspective: the rehearsal lists one camera with its azimuth and pitch and binds a token'; Tool = 'horizun_manage_views' }
        @{ Name = 'perspective: one camera applies and GetOrientation re-reads the eye and directions sent'; Tool = 'horizun_manage_views' }
        @{ Name = 'perspective: a fan of four applies four views 90 degrees apart, each named and re-read'; Tool = 'horizun_manage_views' }
        @{ Name = 'perspective: an eye on its target is refused before writing'; Tool = 'horizun_manage_views' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $V = 'horizun_manage_views'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $names = @('perspective: the rehearsal lists one camera with its azimuth and pitch and binds a token',
                   'perspective: one camera applies and GetOrientation re-reads the eye and directions sent',
                   'perspective: a fan of four applies four views 90 degrees apart, each named and re-read',
                   'perspective: an eye on its target is refused before writing')
        function AllNotCovered($why) { foreach ($n in $names) { Case $n $V 'not_covered' $why } }

        if ($Ctx.WriteGate) { AllNotCovered 'write tier is not open for this run'; return $cases.ToArray() }

        $X = 1120000.0
        $tag = 'HZPV' + $Ctx.RunId
        $created = New-Object System.Collections.Generic.List[long]
        # Eye 8 m south of the target at the same height: looking +Y, azimuth 90, pitch 0.
        $one = @{ operation = 'create_perspective'; key = 'cam'; name = "$tag one"; start = @($X, -8000.0, 1600.0); end = @($X, 0.0, 1600.0) }
        $req = @{ target_document = $doc; units = 'mm'; actions = @($one) }
        $dry = & $Ctx.Call $V ($req + @{ dry_run = $true })
        $pv = if ($dry.data) { @($dry.data.plan)[0].perspective } else { $null }
        $cam = if ($pv) { @($pv.cameras)[0] } else { $null }
        if ($pv -and [int]$pv.views_to_create -eq 1 -and $cam -and [Math]::Abs([double]$cam.azimuth_degrees - 90) -lt 1e-6 -and
            [Math]::Abs([double]$cam.pitch_degrees) -lt 1e-6 -and $dry.data.confirmation_token) {
            Case $names[0] $V 'pass' ('camera=' + ($cam | ConvertTo-Json -Compress -Depth 4))
        } else { Case $names[0] $V 'fail' ('plan=' + ($pv | ConvertTo-Json -Compress -Depth 6) + ' text=' + [string]$dry.text) }

        $ap = & $Ctx.Apply $V $req 'pv-one'
        if (Applied $ap) {
            $row = @($ap.answer.data.rows) | Select-Object -First 1
            $views = @($row.perspective.views)
            foreach ($vw in $views) { if ($vw.view_id) { [void]$created.Add([long]$vw.view_id) } }
            $v0 = $views | Select-Object -First 1
            # Independent of the command's own flag: the eye re-read in feet is the eye asked for in mm.
            $eye = if ($v0 -and $v0.reread) { @($v0.reread.eye_internal_feet) } else { @() }
            $eyeOk = $eye.Count -eq 3 -and [Math]::Abs([double]$eye[0] * 304.8 - $X) -lt 0.01 -and
                     [Math]::Abs([double]$eye[1] * 304.8 + 8000.0) -lt 0.01 -and [Math]::Abs([double]$eye[2] * 304.8 - 1600.0) -lt 0.01
            if ($row.verified -eq $true -and $views.Count -eq 1 -and $v0.verified -eq $true -and $v0.orientation_verified -eq $true -and
                $v0.reread.is_perspective -eq $true -and $v0.reread.name -ceq "$tag one" -and $eyeOk) {
                Case $names[1] $V 'pass' ('re-read: ' + ($v0.reread | ConvertTo-Json -Compress -Depth 4))
            } else { Case $names[1] $V 'fail' ('row=' + ($row | ConvertTo-Json -Compress -Depth 7)) }
        } else { Case $names[1] $V 'fail' (Why $ap) }

        # Same eye and target, four views: azimuths 90, 180, 270 and 0, named by azimuth.
        $fan = @{ operation = 'create_perspective'; name = "$tag fan"; start = @($X, -8000.0, 1600.0); end = @($X, 0.0, 1600.0); fan = 4 }
        $af = & $Ctx.Apply $V @{ target_document = $doc; units = 'mm'; actions = @($fan) } 'pv-fan'
        if (Applied $af) {
            $row = @($af.answer.data.rows) | Select-Object -First 1
            $views = @($row.perspective.views)
            foreach ($vw in $views) { if ($vw.view_id) { [void]$created.Add([long]$vw.view_id) } }
            $az = @($views | ForEach-Object { [int][Math]::Round([double]$_.azimuth_degrees) } | Sort-Object)
            $wanted = @('000', '090', '180', '270' | ForEach-Object { "$tag fan az$_" })
            $named = @($views | Where-Object { $wanted -cnotcontains [string]$_.reread.name }).Count -eq 0
            if ($row.verified -eq $true -and $views.Count -eq 4 -and (($az -join ',') -eq '0,90,180,270') -and $named -and
                @($views | Where-Object { $_.verified -ne $true }).Count -eq 0) {
                Case $names[2] $V 'pass' ('azimuths=' + ($az -join ','))
            } else { Case $names[2] $V 'fail' ('row=' + ($row | ConvertTo-Json -Compress -Depth 7)) }
        } else { Case $names[2] $V 'fail' (Why $af) }

        $bad = @{ operation = 'create_perspective'; start = @($X, 0.0, 1600.0); end = @($X, 0.0, 1600.0) }
        $rf = & $Ctx.Call $V @{ target_document = $doc; units = 'mm'; dry_run = $true; actions = @($bad) }
        $said = [string]$rf.text + ' ' + ($rf.data.errors | ConvertTo-Json -Compress -Depth 4)
        if (($rf.isError -or ($rf.data -and [int]$rf.data.invalid -eq 1)) -and $said -match 'coincide' -and -not $rf.data.confirmation_token) {
            Case $names[3] $V 'pass' ('refused: ' + $said.Substring(0, [Math]::Min(240, $said.Length)))
        } else { Case $names[3] $V 'fail' ('not refused by name: ' + $said) }

        if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'pv-cleanup' }
        return $cases.ToArray()
    }
}
