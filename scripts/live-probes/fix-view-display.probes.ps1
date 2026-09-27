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
                   'view_display: the audit run afterwards no longer produces the finding')
        $tools = @($A, $F, $A)
        function AllNotCovered($why) { for ($i = 0; $i -lt 3; $i++) { Case $names[$i] $tools[$i] 'not_covered' $why } }

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
            $rows = if (Applied $fx) { @($fx.answer.data.rows) } else { @() }
            $okRows = $rows.Count -gt 0 -and @($rows | Where-Object { $_.verified -ne $true }).Count -eq 0
            if ((Applied $fx) -and $fx.answer.data.state -eq 'verified_applied' -and $okRows) {
                Case $names[1] $F 'pass' ("state=verified_applied rows=" + ($rows | ConvertTo-Json -Compress -Depth 6))
                $again = Get-Audit $set
                if ($again.data -and -not (Get-Finding $again)) { Case $names[2] $A 'pass' "probe-detail-level no longer fails on view $viewId" }
                else { Case $names[2] $A 'fail' ('the finding is still produced: ' + [string]$again.text) }
            }
            else {
                Case $names[1] $F 'fail' ((Why $fx) + ' state=' + $fx.answer.data.state)
                Case $names[2] $A 'not_covered' 'the fix did not apply'
            }
        }

        if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'vd-cleanup' }
        return $cases.ToArray()
    }
}
