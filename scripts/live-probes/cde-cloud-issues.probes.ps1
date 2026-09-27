# Live probes for horizun_cde_cloud ACC Issues (src/Horizun.Server/CdeCloudIssues.cs):
# issues_list, the issue_create dry run and - only when explicitly opted in - the apply.
# Loaded by scripts/verify-live.ps1 (see README.md); exercised without Revit or ACC by
# cde-cloud-issues.tests.ps1. The tool is host-resident: nothing here touches the model.
#
# The ACC test project is named, never discovered: HORIZUN_PROBE_ACC_PROJECT_ID (or
# $Ctx.AccProjectId). Without it every case is not_covered - picking "some" project of
# the account would be a guess. The APS credential is the server's own (env vars or the
# 3-legged token file); nothing here reads or passes one.
#
# The apply creates a REAL issue in ACC, which the API cannot delete. It runs only when
# the write tier is open AND HORIZUN_PROBE_ACC_ISSUE_WRITE=1 (or $Ctx.AccIssueWrite) says
# the user approved it for this project; otherwise it is not_covered, by design.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'cde-cloud-issues'
    Catalog = @(
        @{ Name = 'cde_cloud issues: issues_list reads the issues with their subtypes and names what it could not read'; Tool = 'horizun_cde_cloud' }
        @{ Name = 'cde_cloud issues: an issue_create dry run rehearses with a token or refuses with the reason, and creates nothing'; Tool = 'horizun_cde_cloud' }
        @{ Name = 'cde_cloud issues: issue_create applies, reads the issue back and a keyed retry does not duplicate it'; Tool = 'horizun_cde_cloud' }
    )
    Run     = {
        param($Ctx)
        $T = 'horizun_cde_cloud'
        $names = @(
            'cde_cloud issues: issues_list reads the issues with their subtypes and names what it could not read',
            'cde_cloud issues: an issue_create dry run rehearses with a token or refuses with the reason, and creates nothing',
            'cde_cloud issues: issue_create applies, reads the issue back and a keyed retry does not duplicate it'
        )
        $out = New-Object System.Collections.Generic.List[object]
        function Case($i, $ok, $detail) { $out.Add(@{ Name = $names[$i]; Tool = $T; Outcome = $(if ($ok) { 'pass' } else { 'fail' }); Detail = [string]$detail }) }
        function Skip($i, $why) { $out.Add(@{ Name = $names[$i]; Tool = $T; Outcome = 'not_covered'; Detail = [string]$why }) }
        function Excerpt($r) {
            if ($null -eq $r) { return 'no answer' }
            $txt = [string]$r.text
            return ('error=' + $r.isError + ' text=' + $txt.Substring(0, [Math]::Min(300, $txt.Length)))
        }
        function CtxValue($prop, $envName) {
            $p = $Ctx.PSObject.Properties[$prop]
            if ($null -ne $p -and $null -ne $p.Value -and [string]$p.Value -ne '') { return [string]$p.Value }
            return [Environment]::GetEnvironmentVariable($envName)
        }

        $project = CtxValue 'AccProjectId' 'HORIZUN_PROBE_ACC_PROJECT_ID'
        if ([string]::IsNullOrWhiteSpace($project)) {
            foreach ($i in 0..2) { Skip $i 'no ACC test project named: set HORIZUN_PROBE_ACC_PROJECT_ID to a test project GUID' }
            return $out.ToArray()
        }
        # A fixed key (HORIZUN_PROBE_ACC_ISSUE_KEY) keeps a release gate that runs five years to
        # the ONE issue the project owner approved: the first run creates it, every later run
        # proves the keyed create does not duplicate it.
        $fixedKey = CtxValue 'AccIssueKey' 'HORIZUN_PROBE_ACC_ISSUE_KEY'
        $key = if (-not [string]::IsNullOrWhiteSpace($fixedKey)) { [string]$fixedKey } else { 'hz-probe-' + ($Ctx.RunId -replace '[^A-Za-z0-9]', '') }
        if ($key.Length -gt 100) { $key = $key.Substring(0, 100) }

        # ---- case 1: issues_list ----
        $list = & $Ctx.Call $T @{ operation = 'issues_list'; provider = 'acc'; project_id = $project; limit = 5 }
        $noCredential = $false
        if ($list.isError) {
            if ([string]$list.text -match 'credentials are not configured') {
                $noCredential = $true
                Skip 0 ('no APS credential on this machine: ' + (Excerpt $list))
            }
            else { Case 0 $false ('refused: ' + (Excerpt $list)) }
        }
        else {
            $d = $list.data
            $shapeOk = ($null -ne $d) -and ($null -ne $d.issues) -and ($null -ne $d.issue_types) -and ($d.coverage_complete -is [bool])
            # coverage_complete=true with named problems, or false with none, would be a lie either way.
            $honest = $shapeOk -and (($d.coverage_complete -eq $true) -eq (@($d.problems).Count -eq 0))
            Case 0 $honest ('count=' + $d.count + ' types=' + @($d.issue_types).Count + ' coverage_complete=' + $d.coverage_complete +
                            ' problems=' + (@($d.problems) -join ' | '))
        }

        # ---- case 2: the issue_create dry run ----
        # A refusal is NOT a rehearsal: without a credential there is nothing to rehearse
        # (not_covered), and with a subtype that was read any refusal - a wrong scope, a
        # scan error - is a failure. Only the fabricated subtype's own refusal proves it.
        $dry = $null
        $subtype = $null
        $fabricated = $false
        if (-not $list.isError) {
            foreach ($issueType in @($list.data.issue_types)) {
                if ($issueType.is_active -eq $false) { continue }
                $s = @($issueType.subtypes | Where-Object { $_.is_active -ne $false }) | Select-Object -First 1
                if ($null -ne $s) { $subtype = [string]$s.id; break }
            }
        }
        # No readable subtype: an id that cannot exist, so the rehearsal must refuse it.
        if ([string]::IsNullOrWhiteSpace($subtype)) { $subtype = 'hz-probe-no-such-subtype'; $fabricated = $true }
        $create = @{
            operation = 'issue_create'; provider = 'acc'; project_id = $project; external_key = $key
            issue = @{ title = ('Horizun live probe ' + $Ctx.RunId); issue_type_id = $subtype;
                       description = 'Created by the Horizun live probe on a test project; safe to close.' }
        }
        $rehearsed = $false
        if ($noCredential) { $ok2 = $null; $detail2 = 'no APS credential on this machine: nothing to rehearse with' }
        else { $dry = & $Ctx.Call $T $create }
        if ($null -eq $dry) { }
        elseif ($dry.isError) {
            $ok2 = $fabricated -and ([string]$dry.text -match [regex]::Escape($subtype)) -and ([string]$dry.text -match 'Nothing was written')
            $detail2 = 'refused (subtype ' + $(if ($fabricated) { 'fabricated' } else { 'read from the project' }) + '): ' + (Excerpt $dry)
        }
        else {
            $dd = $dry.data
            $rehearsed = $dd.state -eq 'rehearsed' -and $dd.plan.method -eq 'POST' -and ($dd.confirmation_token -or $dd.apply_blocked)
            $ok2 = $rehearsed -or $dd.state -eq 'already_exists'
            $detail2 = 'state=' + $dd.state + ' token=' + [bool]$dd.confirmation_token + ' blocked=' + $dd.apply_blocked +
                       ' scan=' + ($dd.idempotency_scan | ConvertTo-Json -Compress -Depth 3)
        }
        if (-not $noCredential -and -not $list.isError) {
            # Nothing was created: the key is absent after the rehearsal.
            $after = & $Ctx.Call $T @{ operation = 'issues_list'; provider = 'acc'; project_id = $project; external_key = $key }
            if ($after.isError) { $ok2 = $false; $detail2 += ' | re-list refused: ' + (Excerpt $after) }
            elseif ($dry.isError -or $dry.data.state -ne 'already_exists') {
                $ok2 = $ok2 -and ([int]$after.data.count -eq 0)
                $detail2 += ' | issues carrying the key after the dry run=' + $after.data.count
            }
        }
        if ($null -eq $ok2) { Skip 1 $detail2 } else { Case 1 $ok2 $detail2 }

        # ---- case 3: the apply (opt-in) ----
        $optIn = (CtxValue 'AccIssueWrite' 'HORIZUN_PROBE_ACC_ISSUE_WRITE') -in @('1', 'true', 'True')
        $existing = $null
        if ($optIn -and -not $Ctx.WriteGate -and -not $noCredential -and -not [string]::IsNullOrWhiteSpace($fixedKey)) {
            $ex = & $Ctx.Call $T @{ operation = 'issues_list'; provider = 'acc'; project_id = $project; external_key = $key }
            if (-not $ex.isError -and $ex.data) { $existing = @($ex.data.issues | Where-Object { $_ }) | Select-Object -First 1 }
        }
        if ($Ctx.WriteGate) { Skip 2 'write tier closed' }
        elseif ($noCredential) { Skip 2 'no APS credential on this machine' }
        elseif (-not $optIn) {
            Skip 2 'creates a real ACC issue the API cannot delete: run by hand with the user''s approval (HORIZUN_PROBE_ACC_ISSUE_WRITE=1)'
        }
        elseif (-not $rehearsed -or -not $dry.data.confirmation_token) {
            Case 2 $false ('no confirmation token to apply: ' + $detail2)
        }
        elseif ($existing) {
            $retry = & $Ctx.Call $T $create
            $noDup = -not $retry.isError -and $retry.data.state -eq 'already_exists' -and [string]$retry.data.issue_id -eq [string]$existing.issue_id -and $null -eq $retry.data.duplicates
            Case 2 $noDup ('the issue under the fixed key already exists (' + $existing.issue_id + '), created by an earlier run; keyed create: ' +
                           $(if ($retry.isError) { Excerpt $retry } else { 'state=' + $retry.data.state + ' issue=' + $retry.data.issue_id }))
        }
        else {
            $apply = $create.Clone(); $apply['dry_run'] = $false; $apply['confirmation_token'] = $dry.data.confirmation_token
            $a = & $Ctx.Call $T $apply
            $applied = -not $a.isError -and $a.data.state -eq 'applied' -and $a.data.host_verified -eq $true -and $a.data.issue_id
            $retry = & $Ctx.Call $T $create
            $noDup = -not $retry.isError -and $retry.data.state -eq 'already_exists' -and [string]$retry.data.issue_id -eq [string]$a.data.issue_id -and
                     $null -eq $retry.data.duplicates
            Case 2 ($applied -and $noDup) ('apply: ' + $(if ($a.isError) { Excerpt $a } else { 'state=' + $a.data.state + ' host_verified=' + $a.data.host_verified +
                                          ' issue=' + $a.data.issue_id + ' url=' + $a.data.web_url }) +
                                          ' | retry: ' + $(if ($retry.isError) { Excerpt $retry } else { 'state=' + $retry.data.state + ' issue=' + $retry.data.issue_id }))
        }
        return $out.ToArray()
    }
}
