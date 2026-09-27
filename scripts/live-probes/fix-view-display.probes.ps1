# Live probes for horizun_fix_planimetry operation=set_view_display (View.DetailLevel /
# View.Discipline set BECAUSE a finding cites them). The finding comes from an inline
# requirement set whose one rule is pinned by id to an own DUPLICATED floor plan, so it
# can never match the rest of the model. Nothing is saved; the duplicate is deleted.
#
# Shapes of the fix reply (state, rows[].verified, rows[].postconditions) are from the
# code (FixPlanimetryCommand / FixPlanimetryDisplay.cs), to be held against the first
# live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'fix-view-display'
    Catalog = @(
        @{ Name = 'view_display: a requirement set produces a detail_level finding on an own view'; Tool = 'horizun_audit_planimetry' }
        @{ Name = 'view_display: set_view_display applies the cited detail level and re-reads it'; Tool = 'horizun_fix_planimetry' }
        @{ Name = 'view_display: the audit run afterwards no longer produces the finding'; Tool = 'horizun_audit_planimetry' }
        @{ Name = 'view_display: refused by name when the view template CONTROLS the detail level'; Tool = 'horizun_fix_planimetry' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $A = 'horizun_audit_planimetry'; $F = 'horizun_fix_planimetry'
        $names = @('view_display: a requirement set produces a detail_level finding on an own view',
                   'view_display: set_view_display applies the cited detail level and re-reads it',
                   'view_display: the audit run afterwards no longer produces the finding',
                   'view_display: refused by name when the view template CONTROLS the detail level')
        $tools = @($A, $F, $A, $F)
        function AllNotCovered($why) { for ($i = 0; $i -lt 4; $i++) { Case $names[$i] $tools[$i] 'not_covered' $why } }

        if ($Ctx.WriteGate) { AllNotCovered 'write tier is not open for this run'; return $cases.ToArray() }

        $tag = 'HZ_PROBE_VD_' + $Ctx.RunId
        $created = New-Object System.Collections.Generic.List[long]
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $src = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        if (-not $src) { AllNotCovered 'no non-template floor plan to duplicate'; return $cases.ToArray() }
        $dup = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'duplicate_view'; source_view_id = [long]$src.view_id; duplicate_option = 'Duplicate'; name = "HZ_VD_VIEW_$tag"; key = 'v' }) } 'vd-dup-view'
        $viewId = $null
        if (Applied $dup) { $row = @($dup.answer.data.rows) | Select-Object -First 1; if ($row.verified -eq $true) { $viewId = [long]$row.element_id; [void]$created.Add($viewId) } }
        if (-not $viewId) { AllNotCovered ('no own view could be staged: ' + (Why $dup)); return $cases.ToArray() }

        function Get-Set($want) {
            return @{ requirement_set = @{ id = 'horizun-probe-view-display'; version = '1.0.0'; title = 'probe' }
                      rules = @(@{ id = 'probe-detail-level'; entity = 'view'; severity = 'blocking'
                                   selector = @{ applies_to = @($viewId) }
                                   assertion = @{ field = 'detail_level'; operator = 'equals'; value = $want } }) }
        }
        function Get-Audit($set) { & $Ctx.Call $A @{ scope = 'model'; units = 'mm'; max_findings = 500; include_advisory = $true; requirement_set = $set } }
        function Get-Finding($au) {
            if (-not $au.data) { return $null }
            return @($au.data.findings | Where-Object { $_.rule_id -eq 'probe-detail-level' -and $_.status -eq 'failed' -and @($_.element_ids | ForEach-Object { [long]$_ }) -contains $viewId })[0]
        }

        # Whichever level the duplicate does NOT have yet makes the rule fail.
        $want = $null; $set = $null; $au = $null; $finding = $null
        foreach ($candidate in @('Fine', 'Coarse')) {
            $set = Get-Set $candidate; $au = Get-Audit $set; $finding = Get-Finding $au
            if ($finding) { $want = $candidate; break }
        }
        if (-not $finding) {
            Case $names[0] $A 'fail' ('no failed probe-detail-level finding for view ' + $viewId + ': ' + [string]$au.text)
            Case $names[1] $F 'not_covered' 'no finding to cite'; Case $names[2] $A 'not_covered' 'no finding to cite'
            Case $names[3] $F 'not_covered' 'no finding to cite'
        }
        else {
            Case $names[0] $A 'pass' ("finding on view $viewId expecting detail_level=$want")
            $cite = @{ rule_id = $finding.rule_id; requirement_set = $finding.requirement_set
                       requirement_set_version = $finding.requirement_set_version
                       element_ids = @($finding.element_ids | ForEach-Object { [long]$_ }); observed = $finding.observed }
            if ($finding.requirement_set_sha256) { $cite['requirement_set_sha256'] = $finding.requirement_set_sha256 }
            if ($finding.entity_kind) { $cite['entity_kind'] = $finding.entity_kind }
            if ($null -ne $finding.view_id) { $cite['view_id'] = [long]$finding.view_id }
            $fx = & $Ctx.Apply $F @{ target_document = $doc; units = 'mm'; requirement_set = $set
                                     source_audit = @{ finding_set_fingerprint = $au.data.finding_set_fingerprint; units = 'mm' }
                                     actions = @(@{ operation = 'set_view_display'; finding = $cite; view_id = $viewId; detail_level = $want }) } 'vd-fix'
            $rows = @(if (Applied $fx) { $fx.answer.data.rows })
            $okRows = $rows.Count -gt 0 -and @($rows | Where-Object { $_.verified -ne $true }).Count -eq 0
            if ((Applied $fx) -and $fx.answer.data.state -eq 'verified_applied' -and $okRows) {
                Case $names[1] $F 'pass' ("state=verified_applied rows=" + ($rows | ConvertTo-Json -Compress -Depth 6))
                $again = Get-Audit $set
                if ($again.data -and -not (Get-Finding $again)) { Case $names[2] $A 'pass' "probe-detail-level no longer fails on view $viewId" }
                else { Case $names[2] $A 'fail' ('the finding is still produced: ' + [string]$again.text) }

                # A template made FROM the view now carries $want and governs VIEW_DETAIL_LEVEL;
                # applied back to the view, a rule demanding the other level fails, and citing
                # that finding must be refused by name in the rehearsal - nothing is written.
                $other = if ($want -eq 'Fine') { 'Coarse' } else { 'Fine' }
                $t = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                        @{ operation = 'create_template'; view_id = $viewId; name = "HZ_VD_TPL_$tag"; key = 'tpl' }) } 'vd-tpl-create'
                $tpl = if (Applied $t) { $t.answer.data.aliases.tpl } else { $null }
                if (-not $tpl) { Case $names[3] $F 'not_covered' ('no own template: ' + (Why $t)) }
                else {
                    [void]$created.Add([long]$tpl)
                    $g = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                            @{ operation = 'set_template_controls'; view_id = [long]$tpl; parameters = @('VIEW_DETAIL_LEVEL'); controlled = $true }
                            @{ operation = 'apply_template'; view_id = $viewId; template_view_id = [long]$tpl }) } 'vd-tpl-apply'
                    $set2 = Get-Set $other; $au2 = Get-Audit $set2; $f2 = Get-Finding $au2
                    if (-not (Applied $g) -or -not $f2) { Case $names[3] $F 'not_covered' ('template not governing or no finding: ' + (Why $g) + ' | ' + [string]$au2.text) }
                    else {
                        $cite2 = @{ rule_id = $f2.rule_id; requirement_set = $f2.requirement_set; requirement_set_version = $f2.requirement_set_version
                                    element_ids = @($f2.element_ids | ForEach-Object { [long]$_ }); observed = $f2.observed }
                        if ($f2.requirement_set_sha256) { $cite2['requirement_set_sha256'] = $f2.requirement_set_sha256 }
                        if ($f2.entity_kind) { $cite2['entity_kind'] = $f2.entity_kind }
                        if ($null -ne $f2.view_id) { $cite2['view_id'] = [long]$f2.view_id }
                        $rf = & $Ctx.Call $F @{ target_document = $doc; units = 'mm'; dry_run = $true; requirement_set = $set2
                                               source_audit = @{ finding_set_fingerprint = $au2.data.finding_set_fingerprint; units = 'mm' }
                                               actions = @(@{ operation = 'set_view_display'; finding = $cite2; view_id = $viewId; detail_level = $other }) }
                        $said = [string]$rf.text + ' ' + ($rf.data | ConvertTo-Json -Compress -Depth 8)
                        $shown = $said.Substring(0, [Math]::Min(300, $said.Length))
                        if ($said -cmatch 'CONTROLS' -and -not $rf.data.confirmation_token) { Case $names[3] $F 'pass' ('refused: ' + $shown) }
                        else { Case $names[3] $F 'fail' ('not refused by name: ' + $shown) }
                    }
                }
            }
            else {
                Case $names[1] $F 'fail' ((Why $fx) + ' state=' + $fx.answer.data.state)
                Case $names[2] $A 'not_covered' 'the fix did not apply'
                Case $names[3] $F 'not_covered' 'the fix did not apply'
            }
        }

        if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'vd-cleanup' }
        return $cases.ToArray()
    }
}
