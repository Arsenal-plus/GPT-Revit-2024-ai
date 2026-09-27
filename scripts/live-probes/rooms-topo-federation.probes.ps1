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
        return $out.ToArray()
    }
}
