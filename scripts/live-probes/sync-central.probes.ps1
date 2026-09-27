# Live probes for horizun_document_session operation=sync_with_central. Only the
# harness's OWN workshared material is touched: the year's closed-workset fixture
# opened DETACHED (workshared-fixture.lib.ps1), and - for a real sync - a central the
# harness creates from that detached copy in its own scratch folder (SaveAs with
# WorksharingSaveAsOptions.SaveAsCentral=true, then WorksharingUtils.CreateNewLocal),
# because no project central may ever be synchronized by a probe. Creating that central
# needs horizun_execute_python, and syncing needs the owner's sync switch; when either
# is off the case is not_covered with the reason, never forced. Nothing is saved over
# a fixture; the scratch central and local are left in %TEMP% for inspection.
# Shapes from the code (DocumentSessionSync.cs), to be held against the first live run.
. (Join-Path $PSScriptRoot 'workshared-fixture.lib.ps1')
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'sync-central'
    Catalog = @(
        @{ Name = 'sync central: the non-workshared write document is refused as not_workshared'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: a detached workshared copy is refused as detached_copy, nothing ran'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: with the owner switch off the refusal names Advanced options'; Tool = 'horizun_document_session' }
        @{ Name = 'sync central: preview is a labelled estimate, apply syncs a scratch local and verifies relinquish=all'; Tool = 'horizun_document_session' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.ArrayList
        function Case($name, $tool, $outcome, $detail) { [void]$cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $S = 'horizun_document_session'
        $nNotWs = 'sync central: the non-workshared write document is refused as not_workshared'
        $nDetached = 'sync central: a detached workshared copy is refused as detached_copy, nothing ran'
        $nOwnerOff = 'sync central: with the owner switch off the refusal names Advanced options'
        $nReal = 'sync central: preview is a labelled estimate, apply syncs a scratch local and verifies relinquish=all'
        function Code($r) { if ($r.data -and $r.data.code) { [string]$r.data.code } elseif ($r.structured -and $r.structured.code) { [string]$r.structured.code } else { $null } }
        function Short($r) { $t = [string]$r.text; if ($t.Length -gt 300) { $t.Substring(0, 300) } else { $t } }
        if ($Ctx.WriteGate) {
            foreach ($n in @($nNotWs, $nDetached, $nOwnerOff, $nReal)) { Case $n $S 'not_covered' 'the write tier is closed for this run' }
            return $cases
        }
        $run = $Ctx.RunId

        $w = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $Ctx.Document; dry_run = $true }
        $wc = Code $w
        if ($w.isError -and $wc -eq 'not_workshared') { Case $nNotWs $S 'pass' 'refused before any census' }
        elseif ($w.isError -and $wc) { Case $nNotWs $S 'not_covered' "the write document refused as $wc (it may be workshared on this run)" }
        else { Case $nNotWs $S 'fail' ('not refused: ' + (Short $w)) }

        $fixture = Enter-HzWorksharedFixture $Ctx 'sync'
        if (-not $fixture.Title) {
            foreach ($n in @($nDetached, $nOwnerOff, $nReal)) { Case $n $S 'not_covered' $fixture.Why }
            return $cases
        }
        $local = $null
        try {
            $d = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $fixture.Title; dry_run = $true }
            $dc = Code $d
            Case $nDetached $S $(if ($d.isError -and $dc -eq 'detached_copy' -and -not $d.data.confirmation_token) { 'pass' } else { 'fail' }) "code=$dc"

            # A central of the harness's own, in its own scratch folder, and a new local of it.
            $dir = Join-Path ([IO.Path]::GetTempPath()) ('hz-sync-probe-' + $run)
            $py = @"
import os
from Autodesk.Revit.DB import SaveAsOptions, WorksharingSaveAsOptions, WorksharingUtils, ModelPathUtils
d = r'$dir'
if not os.path.isdir(d): os.makedirs(d)
central = os.path.join(d, 'HZ_SYNC_CENTRAL.rvt')
local = os.path.join(d, 'HZ_SYNC_LOCAL.rvt')
o = SaveAsOptions(); o.OverwriteExistingFile = True
w = WorksharingSaveAsOptions(); w.SaveAsCentral = True
o.SetWorksharingOptions(w)
doc.SaveAs(central, o)
WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), ModelPathUtils.ConvertUserVisiblePathToModelPath(local))
__output__ = {'central': central, 'local': local, 'central_title': doc.Title, 'local_exists': os.path.exists(local)}
"@
            $p = & $Ctx.Call 'horizun_execute_python' @{ code = $py; idempotency_key = ('sync-central-' + $run) }
            $out = if ($p.data -and $p.data.__output__) { $p.data.__output__ } elseif ($p.data -and $p.data.output) { $p.data.output } else { $null }
            if ($p.isError -or -not $out -or -not $out.local_exists) {
                $why = 'the harness could not create a scratch central (execute_python off or failed): ' + (Short $p)
                Case $nOwnerOff $S 'not_covered' $why; Case $nReal $S 'not_covered' $why
                return $cases
            }
            $fixture.Title = [string]$out.central_title   # the detached copy became the central
            $o = & $Ctx.Call $S @{ operation = 'open'; file_path = ([string]$out.local).Replace([char]92, '/'); expected_version = [string]$Ctx.Year; idempotency_key = ('sync-open-' + $run) }
            if ($o.isError -or -not $o.data.title) {
                $why = 'the scratch local did not open: ' + (Short $o)
                Case $nOwnerOff $S 'not_covered' $why; Case $nReal $S 'not_covered' $why
                return $cases
            }
            $local = [string]$o.data.title
            $pv = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'all'; comment = ('hz probe ' + $run); dry_run = $true }
            $pc = Code $pv
            if ($pv.isError -and $pc -eq 'sync_not_authorised') {
                Case $nOwnerOff $S $(if ([string]$pv.text -like '*Advanced options*') { 'pass' } else { 'fail' }) 'refused; the owner has not enabled sync on this machine'
                Case $nReal $S 'not_covered' 'the machine owner has not enabled Synchronize with central (Advanced options); a probe never enables it'
                return $cases
            }
            Case $nOwnerOff $S 'not_covered' $(if ($pv.isError) { "refused as $pc" } else { 'the owner switch is ON on this machine' })
            if ($pv.isError -or $pv.data.preview_kind -ne 'estimate' -or -not $pv.data.confirmation_token) {
                Case $nReal $S 'fail' ('the preview is not a labelled estimate with a token: ' + (Short $pv)); return $cases
            }
            $ap = & $Ctx.Call $S @{ operation = 'sync_with_central'; target_document = $local; relinquish = 'all'; comment = ('hz probe ' + $run); dry_run = $false; confirmation_token = [string]$pv.data.confirmation_token; idempotency_key = ('sync-apply-' + $run) }
            $ok = (-not $ap.isError) -and $ap.data.sync_verified -eq $true -and [int]$ap.data.ownership.owned_worksets_after -eq 0 -and [int]$ap.data.ownership.owned_elements_after -eq 0
            Case $nReal $S $(if ($ok) { 'pass' } else { 'fail' }) ('estimate owned_elements=' + $pv.data.owned_elements + ' sample=' + $pv.data.update_status_sample.sample_size + '; apply: ' + (Short $ap))
        }
        finally {
            if ($local) { $null = Exit-HzWorksharedFixture $Ctx @{ Title = $local; WritePath = $fixture.WritePath } 'sync-local' }
            $null = Exit-HzWorksharedFixture $Ctx $fixture 'sync'
        }
        return $cases
    }
}
