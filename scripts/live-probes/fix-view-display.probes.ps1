# Live probes for horizun_fix_planimetry operation=set_view_display (View.DetailLevel /
# View.Discipline set BECAUSE a finding cites them). Each finding comes from an inline
# requirement set whose one rule is pinned by id to an own DUPLICATED floor plan, so it
# can never match the rest of the model. Duplicate copies the source's view template, so
# the duplicate's template is cleared FIRST: a model template that controls Detail Level
# or Discipline would otherwise refuse the apply cases for a reason that is not the
# product's. The template cases then use an OWN template. Nothing is saved; the
# duplicate and the template are deleted.
#
# Shapes of the fix reply (state, rows[].verified), of the audit finding (rule_id,
# status, element_ids, observed {field, value}) and of the refusals ('CONTROLS', 'only
# when the cited finding is about <property>') are from the code (FixPlanimetryCommand /
# FixPlanimetryDisplay.cs / PlanimetryRules.cs), to be held against the first live run.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'fix-view-display'
    Catalog = @(
        @{ Name = 'view_display: a requirement set produces a detail_level finding on an own view'; Tool = 'horizun_audit_planimetry' }
        @{ Name = 'view_display: set_view_display applies the cited detail level and re-reads it'; Tool = 'horizun_fix_planimetry' }
        @{ Name = 'view_display: the audit run afterwards no longer produces the finding'; Tool = 'horizun_audit_planimetry' }
        @{ Name = 'view_display: set_view_display applies a cited discipline and re-reads it'; Tool = 'horizun_fix_planimetry' }
        @{ Name = 'view_display: a detail_level finding does not license a discipline change'; Tool = 'horizun_fix_planimetry' }
        @{ Name = 'view_display: refused by name when the view template CONTROLS the detail level'; Tool = 'horizun_fix_planimetry' }
        @{ Name = 'view_display: refused by name when the view template CONTROLS the discipline'; Tool = 'horizun_fix_planimetry' }
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
                   'view_display: set_view_display applies a cited discipline and re-reads it',
                   'view_display: a detail_level finding does not license a discipline change',
                   'view_display: refused by name when the view template CONTROLS the detail level',
                   'view_display: refused by name when the view template CONTROLS the discipline')
        $tools = @($A, $F, $A, $F, $F, $F, $F)
        function Rest($from, $why) { for ($i = $from; $i -lt $names.Count; $i++) { Case $names[$i] $tools[$i] 'not_covered' $why } }

        if ($Ctx.WriteGate) { Rest 0 'write tier is not open for this run'; return $cases.ToArray() }

        $tag = 'HZ_PROBE_VD_' + $Ctx.RunId
        $created = New-Object System.Collections.Generic.List[long]
        function Cleanup { if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'vd-cleanup' } }
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $src = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        if (-not $src) { Rest 0 'no non-template floor plan to duplicate'; return $cases.ToArray() }
        $dup = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'duplicate_view'; source_view_id = [long]$src.view_id; duplicate_option = 'Duplicate'; name = "HZ_VD_VIEW_$tag"; key = 'v' }) } 'vd-dup-view'
        $viewId = $null
        if (Applied $dup) { $row = @($dup.answer.data.rows) | Select-Object -First 1; if ($row.verified -eq $true) { $viewId = [long]$row.element_id; [void]$created.Add($viewId) } }
        if (-not $viewId) { Rest 0 ('no own view could be staged: ' + (Why $dup)); return $cases.ToArray() }
        $clr = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'apply_template'; view_id = $viewId; template_view_id = -1 }) } 'vd-clear-tpl'
        if (-not (Applied $clr)) { Rest 0 ('the duplicate''s view template could not be cleared: ' + (Why $clr)); Cleanup; return $cases.ToArray() }

        function Get-Set($field, $want, $ruleId) {
            return @{ requirement_set = @{ id = 'horizun-probe-view-display'; version = '1.0.0'; title = 'probe' }
                      rules = @(@{ id = $ruleId; entity = 'view'; severity = 'blocking'
                                   selector = @{ applies_to = @($viewId) }
                                   assertion = @{ field = $field; operator = 'equals'; value = $want } }) }
        }
        function Get-Audit($set) { & $Ctx.Call $A @{ scope = 'model'; units = 'mm'; max_findings = 500; include_advisory = $true; requirement_set = $set } }
        function Get-Finding($au, $ruleId) {
            if (-not $au.data) { return $null }
            return @($au.data.findings | Where-Object { $_.rule_id -eq $ruleId -and $_.status -eq 'failed' -and @($_.element_ids | ForEach-Object { [long]$_ }) -contains $viewId })[0]
        }
        # The first candidate value the view does NOT have yet makes the rule fail.
        function Find-Failing($field, $ruleId, $candidates) {
            $set = $null; $au = $null
            foreach ($c in $candidates) {
                $set = Get-Set $field $c $ruleId; $au = Get-Audit $set; $f = Get-Finding $au $ruleId
                if ($f) { return @{ want = $c; set = $set; audit = $au; finding = $f } }
            }
            return @{ want = $null; set = $set; audit = $au; finding = $null }
        }
        function Cite($f) {
            $c = @{ rule_id = $f.rule_id; requirement_set = $f.requirement_set; requirement_set_version = $f.requirement_set_version
                    element_ids = @($f.element_ids | ForEach-Object { [long]$_ }); observed = $f.observed }
            if ($f.requirement_set_sha256) { $c['requirement_set_sha256'] = $f.requirement_set_sha256 }
            if ($f.entity_kind) { $c['entity_kind'] = $f.entity_kind }
            if ($null -ne $f.view_id) { $c['view_id'] = [long]$f.view_id }
            return $c
        }
        # An apply with a key; without one, a rehearsal (dry_run) through Call.
        function Fix($hit, $action, $key) {
            $action['operation'] = 'set_view_display'; $action['finding'] = (Cite $hit.finding); $action['view_id'] = $viewId
            $body = @{ target_document = $doc; units = 'mm'; requirement_set = $hit.set; actions = @($action)
                       source_audit = @{ finding_set_fingerprint = $hit.audit.data.finding_set_fingerprint; units = 'mm' } }
            if ($key) { return & $Ctx.Apply $F $body $key }
            $body['dry_run'] = $true
            return & $Ctx.Call $F $body
        }
        function Verified($fx) {
            $rows = @(if (Applied $fx) { $fx.answer.data.rows })
            return (Applied $fx) -and $fx.answer.data.state -eq 'verified_applied' -and $rows.Count -gt 0 -and @($rows | Where-Object { $_.verified -ne $true }).Count -eq 0
        }
        # A refusal in the rehearsal: the expected words, and no confirmation token.
        function Refused($rf, $pattern) {
            $said = [string]$rf.text + ' ' + ($rf.data | ConvertTo-Json -Compress -Depth 8)
            $shown = $said.Substring(0, [Math]::Min(300, $said.Length))
            if ($said -cmatch $pattern -and -not $rf.data.confirmation_token) { return @('pass', ('refused: ' + $shown)) }
            return @('fail', ('not refused by name: ' + $shown))
        }

        # ---- detail level: finding, fix, re-audit.
        $hit = Find-Failing 'detail_level' 'probe-detail-level' @('Fine', 'Coarse')
        if (-not $hit.finding) {
            Case $names[0] $A 'fail' ('no failed probe-detail-level finding for view ' + $viewId + ': ' + [string]$hit.audit.text)
            Rest 1 'no detail_level finding to cite'; Cleanup; return $cases.ToArray()
        }
        $want = $hit.want
        Case $names[0] $A 'pass' ("finding on view $viewId expecting detail_level=$want")
        $fx = Fix $hit @{ detail_level = $want } 'vd-fix'
        if (-not (Verified $fx)) {
            Case $names[1] $F 'fail' ((Why $fx) + ' state=' + $fx.answer.data.state)
            Rest 2 'the detail-level fix did not apply'; Cleanup; return $cases.ToArray()
        }
        Case $names[1] $F 'pass' ('state=verified_applied rows=' + ($fx.answer.data.rows | ConvertTo-Json -Compress -Depth 6))
        $again = Get-Audit $hit.set
        if ($again.data -and -not (Get-Finding $again 'probe-detail-level')) { Case $names[2] $A 'pass' "probe-detail-level no longer fails on view $viewId" }
        else { Case $names[2] $A 'fail' ('the finding is still produced: ' + [string]$again.text) }

        # ---- discipline, applied and re-read (the view has no template now).
        $dh = Find-Failing 'discipline' 'probe-discipline' @('Coordination', 'Architectural')
        if (-not $dh.finding) { Case $names[3] $F 'fail' ('no failed probe-discipline finding: ' + [string]$dh.audit.text) }
        else {
            $dx = Fix $dh @{ discipline = $dh.want } 'vd-fix-discipline'
            if (Verified $dx) { Case $names[3] $F 'pass' ("discipline=$($dh.want) rows=" + ($dx.answer.data.rows | ConvertTo-Json -Compress -Depth 6)) }
            else { Case $names[3] $F 'fail' ((Why $dx) + ' state=' + $dx.answer.data.state) }
        }

        # ---- a detail_level finding cited for a discipline change: refused, nothing written.
        $other = if ($want -eq 'Fine') { 'Coarse' } else { 'Fine' }
        $oh = Find-Failing 'detail_level' 'probe-detail-level' @($other)
        if (-not $oh.finding) { Case $names[4] $F 'not_covered' ('no detail_level finding to cite: ' + [string]$oh.audit.text) }
        else { $v = Refused (Fix $oh @{ detail_level = $other; discipline = 'Structural' } $null) 'about discipline'; Case $names[4] $F $v[0] $v[1] }

        # ---- an OWN template made from the view, controlling both parameters, applied back.
        $t = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                @{ operation = 'create_template'; view_id = $viewId; name = "HZ_VD_TPL_$tag"; key = 'tpl' }) } 'vd-tpl-create'
        $tpl = if (Applied $t) { $t.answer.data.aliases.tpl } else { $null }
        if (-not $tpl) { Rest 5 ('no own template: ' + (Why $t)); Cleanup; return $cases.ToArray() }
        [void]$created.Add([long]$tpl)
        $g = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(
                @{ operation = 'set_template_controls'; view_id = [long]$tpl; parameters = @('VIEW_DETAIL_LEVEL', 'VIEW_DISCIPLINE'); controlled = $true }
                @{ operation = 'apply_template'; view_id = $viewId; template_view_id = [long]$tpl }) } 'vd-tpl-apply'
        if (-not (Applied $g)) { Rest 5 ('the own template could not be applied: ' + (Why $g)); Cleanup; return $cases.ToArray() }
        $h5 = Find-Failing 'detail_level' 'probe-detail-level' @($other)
        if (-not $h5.finding) { Case $names[5] $F 'not_covered' ('no detail_level finding under the template: ' + [string]$h5.audit.text) }
        else { $v = Refused (Fix $h5 @{ detail_level = $other } $null) 'CONTROLS'; Case $names[5] $F $v[0] $v[1] }
        $h6 = Find-Failing 'discipline' 'probe-discipline' @('Architectural', 'Coordination')
        if (-not $h6.finding) { Case $names[6] $F 'not_covered' ('no discipline finding under the template: ' + [string]$h6.audit.text) }
        else { $v = Refused (Fix $h6 @{ discipline = $h6.want } $null) 'CONTROLS'; Case $names[6] $F $v[0] $v[1] }

        Cleanup
        return $cases.ToArray()
    }
}
