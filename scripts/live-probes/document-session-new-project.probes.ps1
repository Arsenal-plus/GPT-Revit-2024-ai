# Live probes for horizun_document_session operation=new_project (course dry run
# 2026-09-30, defect #18) and for horizun_open_document activating a document that
# is already open (defect #13).
#
# new_project: the rehearsal (dry_run omitted, so it defaults to true) must resolve a
# template - Revit's own DefaultProjectTemplate first, and when this machine has none
# configured, Autodesk's own template for the year found BY PATH (the cobie probe's
# rule) - issue a token and create nothing. An existing target must be refused and
# left byte for byte. The apply must write the .rvt to the run's scratch folder,
# re-read it (this Revit's year off its header, the document's path) and report
# whether it became the ACTIVE document - which is what this probe exists to measure,
# because the activation of a document NewProjectDocument created in the background
# through OpenAndActivateDocument has never run in Revit.
#
# open_document over the new project, which is then open AND active: the activation
# path must answer without allow_upgrade, version_guard='not_applicable_already_open'
# and upgraded_on_open=false. (The upgraded-in-memory case of the course run needs an
# older model; this proves the activation branch itself.)
#
# The new project is closed without saving (it was never modified after its own
# SaveAs) and the write document re-activated, through the shared fixture helper.
. (Join-Path $PSScriptRoot 'workshared-fixture.lib.ps1')

$script:HzProbeModules += [pscustomobject]@{
    Name    = 'document-session-new-project'
    Catalog = @(
        @{ Name = 'new_project: the rehearsal resolves a template, issues a token and creates nothing'; Tool = 'horizun_document_session' }
        @{ Name = 'new_project: an existing target is refused and left byte for byte'; Tool = 'horizun_document_session' }
        @{ Name = 'new_project: the apply writes the .rvt, re-reads it as this Revit year and the new project is ACTIVE'; Tool = 'horizun_document_session' }
        @{ Name = 'open_document: activating the already-open new project needs no allow_upgrade (not_applicable_already_open)'; Tool = 'horizun_open_document' }
        @{ Name = 'new_project probes: the new project is closed and the write document re-activated'; Tool = 'horizun_document_session' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $S = 'horizun_document_session'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $names = @($script:HzProbeModules | Where-Object { $_.Name -eq 'document-session-new-project' } | Select-Object -First 1).Catalog | ForEach-Object { $_.Name }
        $tools = @($S, $S, $S, 'horizun_open_document', $S)

        if ($Ctx.WriteGate) {
            for ($i = 0; $i -lt $names.Count; $i++) { Case $names[$i] $tools[$i] 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        # The write document must be given back as the ACTIVE one afterwards, so its path is
        # read first; without it nothing is created at all.
        $h = & $Ctx.Call 'horizun_health' @{}
        $me = @($h.data.open_documents | Where-Object { $_.title -eq $Ctx.Document }) | Select-Object -First 1
        if (-not $me -or -not $me.path) {
            for ($i = 0; $i -lt $names.Count; $i++) { Case $names[$i] $tools[$i] 'not_covered' "the write document's path is not readable from health, so it could not be re-activated afterwards; nothing was created" }
            return $cases.ToArray()
        }
        $writePath = [string]$me.path

        $run = ([string]$Ctx.RunId) -replace '[^A-Za-z0-9]', ''
        $folder = Join-Path $Ctx.ScratchRoot ('hz-newproj-' + $run)
        $null = New-Item -ItemType Directory -Force -Path $folder
        $target = Join-Path $folder ('HZ_NEW_' + $run + '.rvt')

        # ---- 1: the rehearsal, with Revit's default template, else Autodesk's own by path ----
        $req = @{ operation = 'new_project'; save_as_path = $target.Replace([char]92, '/') }
        $dry = & $Ctx.Call $S $req
        if ($dry.isError -and [string]$dry.text -match 'template') {
            $tplRoot = if ($Ctx.PSObject.Properties['TemplateRoot'] -and $Ctx.TemplateRoot) { [string]$Ctx.TemplateRoot } else { 'C:\ProgramData\Autodesk\RVT ' + $Ctx.Year + '\Templates' }
            $tpl = @(@('English\DefaultMetric.rte', 'English\Default-Multi-Discipline_Metric.rte', 'English_I\DefaultMetric.rte') |
                     ForEach-Object { Join-Path $tplRoot $_ } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
            if ($tpl) { $req['template_path'] = $tpl.Replace([char]92, '/'); $dry = & $Ctx.Call $S $req }
        }
        $rehearsed = -not $dry.isError -and $dry.data -and $dry.data.dry_run -eq $true -and $dry.data.confirmation_token -and
                     $dry.data.template_source -and -not (Test-Path -LiteralPath $target)
        if ($rehearsed) { Case $names[0] $S 'pass' ('template_source=' + $dry.data.template_source + ' template=' + $dry.data.template_path) }
        else { Case $names[0] $S 'fail' ('rehearsal: ' + [string]$dry.text) }

        # ---- 2: an existing target is refused and untouched ----
        $occupied = Join-Path $folder ('HZ_OCCUPIED_' + $run + '.rvt')
        [IO.File]::WriteAllBytes($occupied, [byte[]](1, 2, 3, 4))
        $occ = $req.Clone(); $occ['save_as_path'] = $occupied.Replace([char]92, '/')
        $refused = & $Ctx.Call $S $occ
        $bytes = [IO.File]::ReadAllBytes($occupied)
        if ($refused.isError -and [string]$refused.text -match 'NEVER overwrites' -and $bytes.Length -eq 4) {
            Case $names[1] $S 'pass' 'refused by name; the 4-byte file is unchanged'
        } else { Case $names[1] $S 'fail' ('expected a never-overwrite refusal: ' + [string]$refused.text + ' bytes=' + $bytes.Length) }

        # ---- 3: the apply ----
        $created = $null
        if ($rehearsed) {
            $apply = $req.Clone()
            $apply['dry_run'] = $false
            $apply['confirmation_token'] = [string]$dry.data.confirmation_token
            $apply['idempotency_key'] = 'live-new-project-' + $run
            $ap = & $Ctx.Call $S $apply
            $d = $ap.data
            if (-not $ap.isError -and $d -and $d.created -eq $true) { $created = $d }
            $onDisk = Test-Path -LiteralPath $target
            if ($created -and $onDisk -and [string]$d.file.revit_version -eq [string]$Ctx.Year -and
                $d.path_matches_request -eq $true -and $d.activated -eq $true -and $d.active_document_verified -eq $true) {
                Case $names[2] $S 'pass' ('created ' + $d.title + ' (' + $d.file.bytes + ' bytes, Revit ' + $d.file.revit_version + '), activated')
            }
            elseif ($created) {
                Case $names[2] $S 'fail' ('created but not fully proven: on_disk=' + $onDisk + ' version=' + $d.file.revit_version +
                                          ' path_matches=' + $d.path_matches_request + ' activated=' + $d.activated + ' - ' + $d.activation_note)
            }
            else { Case $names[2] $S 'fail' ('apply: ' + [string]$ap.text) }
        }
        else { Case $names[2] $S 'unverified' 'the rehearsal did not issue a token, so nothing was applied' }

        # ---- 4: open_document over the open, active new project ----
        if ($created -and $created.activated -eq $true) {
            $o = & $Ctx.Call 'horizun_open_document' @{ path = $target.Replace([char]92, '/'); expected_version = [string]$Ctx.Year
                                                       idempotency_key = 'live-new-project-activate-' + $run }
            if (-not $o.isError -and $o.data -and $o.data.already_open -eq $true -and
                $o.data.version_guard -eq 'not_applicable_already_open' -and $o.data.upgraded_on_open -eq $false -and
                $o.data.confirmed_active -eq $true) {
                Case $names[3] 'horizun_open_document' 'pass' ('status=' + $o.data.status + ' version_guard=' + $o.data.version_guard)
            } else { Case $names[3] 'horizun_open_document' 'fail' ('expected an activation-only answer: ' + [string]$o.text) }
        }
        else { Case $names[3] 'horizun_open_document' 'unverified' 'no active new project to activate again' }

        # ---- 5: close it and give the write document back ----
        if ($created) {
            $out = Exit-HzWorksharedFixture $Ctx @{ Title = [string]$created.title; WritePath = $writePath } 'newproj'
            if ($out -match '^fixture closed without saving; write document re-activated=True') { Case $names[4] $S 'pass' $out }
            else { Case $names[4] $S 'fail' $out }
        }
        else { Case $names[4] $S 'not_covered' 'nothing was created, so there is nothing to close' }

        return $cases.ToArray()
    }
}
