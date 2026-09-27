# Live probes for horizun_manage_views operation=renumber_sheets (a map old -> new
# sheet number, one transaction, collisions refused before writing, cycles parked on
# a temporary number). Stages three OWN sheets without a title block, numbered with
# this run's id so no existing sheet can hold them; nothing else is renumbered.
# Nothing is saved; the sheets are deleted at the end.
#
# Reply shapes (plan[i].renumber.steps/temporary_steps, rows[i].renumber.renumbered[]
# with reread/verified, invalid/errors on a refused rehearsal) are from the code
# (ManageViewsRenumber.cs / ManageViewsCommand.cs), to be held against the first live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'manage-views-renumber'
    Catalog = @(
        @{ Name = 'renumber: the rehearsal of a swap plus a shift shows one temporary step and binds a token'; Tool = 'horizun_manage_views' }
        @{ Name = 'renumber: the swap plus shift applies in one transaction and every number re-reads'; Tool = 'horizun_manage_views' }
        @{ Name = 'renumber: a target held by a sheet outside the map is refused before writing'; Tool = 'horizun_manage_views' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $V = 'horizun_manage_views'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $names = @('renumber: the rehearsal of a swap plus a shift shows one temporary step and binds a token',
                   'renumber: the swap plus shift applies in one transaction and every number re-reads',
                   'renumber: a target held by a sheet outside the map is refused before writing')
        function AllNotCovered($why) { foreach ($n in $names) { Case $n $V 'not_covered' $why } }

        if ($Ctx.WriteGate) { AllNotCovered 'write tier is not open for this run'; return $cases.ToArray() }

        $p = 'HZRN' + $Ctx.RunId + '-'
        $n1 = $p + '1'; $n2 = $p + '2'; $n3 = $p + '3'; $n4 = $p + '4'
        $created = New-Object System.Collections.Generic.List[long]
        $mk = & $Ctx.Apply $V @{ target_document = $doc; actions = @(
                @{ operation = 'create_sheet'; number = $n1; name = 'HZ RN one'; key = 's1' }
                @{ operation = 'create_sheet'; number = $n2; name = 'HZ RN two'; key = 's2' }
                @{ operation = 'create_sheet'; number = $n3; name = 'HZ RN three'; key = 's3' }) } 'rn-stage'
        $ids = @{}
        if (Applied $mk) { foreach ($k in 's1', 's2', 's3') { $sid = $mk.answer.data.aliases.$k; if ($sid) { $ids[$k] = [long]$sid; [void]$created.Add([long]$sid) } } }
        if ($ids.Count -ne 3) {
            AllNotCovered ('three own sheets could not be staged: ' + (Why $mk))
            if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'rn-cleanup' }
            return $cases.ToArray()
        }

        # 1 <-> 2 is a closed cycle (one temporary), 3 -> 4 is a plain move.
        $map = [ordered]@{}; $map[$n1] = $n2; $map[$n2] = $n1; $map[$n3] = $n4
        $req = @{ target_document = $doc; actions = @(@{ operation = 'renumber_sheets'; renumber = $map }) }
        $dry = & $Ctx.Call $V ($req + @{ dry_run = $true })
        $plan = if ($dry.data) { @($dry.data.plan)[0].renumber } else { $null }
        if ($plan -and [int]$plan.temporary_steps -eq 1 -and @($plan.steps).Count -eq 4 -and $dry.data.confirmation_token) {
            Case $names[0] $V 'pass' ('steps=' + (@($plan.steps | ForEach-Object { "$($_.from)->$($_.to)" }) -join ', '))
        } else { Case $names[0] $V 'fail' ('plan=' + ($plan | ConvertTo-Json -Compress -Depth 6) + ' text=' + [string]$dry.text) }

        $ap = & $Ctx.Apply $V $req 'rn-apply'
        if (Applied $ap) {
            $row = @($ap.answer.data.rows) | Select-Object -First 1
            $moved = @($row.renumber.renumbered)
            $want = @{ ([string]$ids.s1) = $n2; ([string]$ids.s2) = $n1; ([string]$ids.s3) = $n4 }
            $allRight = $moved.Count -eq 3 -and @($moved | Where-Object { $_.verified -ne $true -or $_.reread -cne $want[[string]$_.sheet_id] }).Count -eq 0
            # An independent read: a rehearsal naming the NEW numbers as old ones is only
            # valid if the document really holds them now.
            $back = [ordered]@{}; $back[$n2] = $n1; $back[$n1] = $n2; $back[$n4] = $n3
            $check = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'renumber_sheets'; renumber = $back }) }
            if ($row.verified -eq $true -and $allRight -and $check.data -and [int]$check.data.invalid -eq 0) {
                Case $names[1] $V 'pass' ('re-read: ' + ($moved | ConvertTo-Json -Compress -Depth 4))
            } else { Case $names[1] $V 'fail' ('row=' + ($row | ConvertTo-Json -Compress -Depth 6) + ' back=' + [string]$check.text) }
        } else { Case $names[1] $V 'fail' (Why $ap) }

        # $n4 now names sheet s3, which this map does not move: refused, nothing issued.
        $bad = [ordered]@{}; $bad[$n1] = $n4
        $rf = & $Ctx.Call $V @{ target_document = $doc; dry_run = $true; actions = @(@{ operation = 'renumber_sheets'; renumber = $bad }) }
        $said = [string]$rf.text + ' ' + ($rf.data.errors | ConvertTo-Json -Compress -Depth 4)
        if (($rf.isError -or ($rf.data -and [int]$rf.data.invalid -eq 1)) -and $said -match 'does not move' -and -not $rf.data.confirmation_token) {
            Case $names[2] $V 'pass' ('refused: ' + $said.Substring(0, [Math]::Min(240, $said.Length)))
        } else { Case $names[2] $V 'fail' ('not refused by name: ' + $said) }

        $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'rn-cleanup'
        return $cases.ToArray()
    }
}
