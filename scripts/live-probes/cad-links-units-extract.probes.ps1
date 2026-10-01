# Live probes for three defects of the 2026-09-30 dry run, on one DWG this module makes:
#
#   #1  horizun_manage_cad_links add compared NOTHING: units=millimeter, a link declaring
#       inch, and the reply said verified_applied. Now the reply carries units_check and a
#       disagreement withholds verified. The probe links an own exported DWG once with no
#       unit (nothing to compare - must still verify), then the SAME file again with a unit
#       the first link does not declare. Revit reuses the first link's CADLinkType (measured,
#       verify-dwg-cadlink.ps1 W3) and records the drawing's own unit (W2), so the second
#       link must come back units_check.verdict='disagrees' and NOT verified_applied.
#   #8  horizun_cad_extract with view_id threw 'DetailLevel is already set' (the harvest set
#       Options.DetailLevel and then Options.View). The probe reads the own link scoped to
#       the view it was linked into.
#   #17 horizun_query_cad mode=profile answered 78 kB. The probe reads the same link with
#       response_mode=compact and checks it stays under 20 kB and counts the same layers as
#       full; and that compact is refused for a mode that has no compact shape.
#
# Reply shapes (units_check.verdict, host_verified, application.state, failed_postconditions;
# response_mode / layers_profiled) are from the code (ManageCadLinksCommand.cs,
# CadLinkUnitRules.cs, QueryCadCommand.cs, CadLayerProfileRules.cs), to be held against the
# first live run. Nothing is saved; both links are deleted.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'cad-links-units-extract'
    Catalog = @(
        @{ Name = 'cad_links: add with no unit asked for verifies (nothing to compare)'; Tool = 'horizun_manage_cad_links' }
        @{ Name = 'cad_links: add with a unit the link does not declare is NOT verified_applied (units_check disagrees)'; Tool = 'horizun_manage_cad_links' }
        @{ Name = 'cad_extract: a view-scoped read does not throw DetailLevel is already set'; Tool = 'horizun_cad_extract' }
        @{ Name = 'query_cad: profile response_mode=compact stays under 20 kB and profiles the same layers as full'; Tool = 'horizun_query_cad' }
        @{ Name = 'query_cad: response_mode=compact outside mode=profile is refused by name'; Tool = 'horizun_query_cad' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        function Said($reply) { [string]$reply.text + ' ' + ($reply.data | ConvertTo-Json -Compress -Depth 12) }
        function Bytes($reply) {
            $body = if ($reply.text) { [string]$reply.text } else { $reply.data | ConvertTo-Json -Compress -Depth 50 }
            [Text.Encoding]::UTF8.GetByteCount($body)
        }
        $doc = $Ctx.Document
        $L = 'horizun_manage_cad_links'; $X = 'horizun_cad_extract'; $Q = 'horizun_query_cad'
        $names = @('cad_links: add with no unit asked for verifies (nothing to compare)',
                   'cad_links: add with a unit the link does not declare is NOT verified_applied (units_check disagrees)',
                   'cad_extract: a view-scoped read does not throw DetailLevel is already set',
                   'query_cad: profile response_mode=compact stays under 20 kB and profiles the same layers as full',
                   'query_cad: response_mode=compact outside mode=profile is refused by name')
        $tools = @($L, $L, $X, $Q, $Q)
        # The last case (the refusal) is always measured and added by Finish; Rest covers the others.
        function Rest($from, $why) { for ($i = $from; $i -lt $names.Count - 1; $i++) { Case $names[$i] $tools[$i] 'not_covered' $why } }

        # The refusal needs no drawing and writes nothing.
        $refuse = & $Ctx.Call $Q @{ mode = 'layers'; instance_id = 1; response_mode = 'compact' }
        $refusalCase = if ($refuse.isError -and (Said $refuse) -match 'applies to mode=profile only') { @('pass', 'refused: ' + [string]$refuse.text) }
                       else { @('fail', 'not refused by name: ' + (Said $refuse)) }

        $created = New-Object System.Collections.Generic.List[long]
        function Cleanup { if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'cu-cleanup' } }
        function Finish { Cleanup; Case $names[4] $Q $refusalCase[0] $refusalCase[1]; return $cases.ToArray() }

        if ($Ctx.WriteGate) { Rest 0 'write tier is not open for this run'; return (Finish) }

        # ---- stage: an own DWG exported from a floor plan of the write document.
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $plan = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        if (-not $plan) { Rest 0 'no non-template floor plan to export and link into'; return (Finish) }
        $viewId = [long]$plan.view_id
        $stem = 'HZ_PROBE_CU_' + $Ctx.RunId
        $dwg = Join-Path $Ctx.ScratchRoot ($stem + '.dwg')
        $ex = & $Ctx.Apply 'horizun_export' @{ target_document = $doc; format = 'dwg'; view_ids = @($viewId); output_path = $dwg } 'cu-export'
        $file = @(Get-ChildItem -LiteralPath $Ctx.ScratchRoot -Filter ($stem + '*.dwg') -ErrorAction SilentlyContinue)[0]
        if (-not $file) { Rest 0 ('no DWG was exported to link: ' + (Why $ex)); return (Finish) }

        # ---- #1a: no unit asked for. Nothing to compare; must verify as before.
        $a = & $Ctx.Apply $L @{ target_document = $doc; operation = 'add'; view_id = $viewId; file_path = $file.FullName; current_view_only = $true } 'cu-add-default'
        $firstId = $null; $declared = $null
        if (Applied $a) {
            $firstId = [long]$a.answer.data.element_id; [void]$created.Add($firstId)
            $declared = [string]$a.answer.data.instance.declared_units
            $uc = $a.answer.data.units_check
            if ($uc.verdict -eq 'not_requested' -and $a.answer.data.host_verified -eq $true -and $a.answer.data.application.state -eq 'verified_applied') {
                Case $names[0] $L 'pass' ("declared=$declared units_check=" + ($uc | ConvertTo-Json -Compress))
            } else {
                Case $names[0] $L 'fail' ("state=$($a.answer.data.application.state) host_verified=$($a.answer.data.host_verified) units_check=" + ($uc | ConvertTo-Json -Compress))
            }
        } else { Case $names[0] $L 'fail' (Why $a) }
        if (-not $firstId) { Rest 1 'the first link was not created'; return (Finish) }

        # ---- #1b: the same file again, forced to a unit the link does not declare.
        $forcedUnit = if ($declared -eq 'millimeter') { 'inch' } else { 'millimeter' }
        $b = & $Ctx.Apply $L @{ target_document = $doc; operation = 'add'; view_id = $viewId; file_path = $file.FullName
                                units = $forcedUnit; allow_duplicate = $true; current_view_only = $true } 'cu-add-forced'
        if (Applied $b) {
            [void]$created.Add([long]$b.answer.data.element_id)
            $d = $b.answer.data
            $ok = $d.units_check.verdict -eq 'disagrees' -and $d.host_verified -eq $false -and
                  $d.application.state -ne 'verified_applied' -and @($d.failed_postconditions) -contains 'units_disagree'
            $detail = "asked=$forcedUnit declared=$($d.instance.declared_units) state=$($d.application.state) units_check=" + ($d.units_check | ConvertTo-Json -Compress)
            if ($ok) { Case $names[1] $L 'pass' $detail }
            elseif ($d.units_check.verdict -eq 'agrees') { Case $names[1] $L 'unverified' ('the link declared the forced unit - the W2/W3 measurement did not reproduce, so there was no disagreement to report: ' + $detail) }
            else { Case $names[1] $L 'fail' $detail }
        } else { Case $names[1] $L 'fail' (Why $b) }

        # ---- #8: the view-scoped extract.
        $xr = & $Ctx.Call $X @{ instance_id = $firstId; view_id = $viewId }
        if ($xr.isError) {
            $why = Said $xr
            if ($why -match 'DetailLevel is already set') { Case $names[2] $X 'fail' ('still throws: ' + $why.Substring(0, [Math]::Min(300, $why.Length))) }
            else { Case $names[2] $X 'fail' ('refused for another reason: ' + $why.Substring(0, [Math]::Min(300, $why.Length))) }
        } else { Case $names[2] $X 'pass' ('read ' + (Bytes $xr) + ' B scoped to view ' + $viewId) }

        # ---- #17: compact against full, on the same link.
        $full = & $Ctx.Call $Q @{ mode = 'profile'; instance_id = $firstId }
        $compact = & $Ctx.Call $Q @{ mode = 'profile'; instance_id = $firstId; response_mode = 'compact' }
        if ($full.isError -or $compact.isError) { Case $names[3] $Q 'fail' ('full: ' + [string]$full.text + ' compact: ' + [string]$compact.text) }
        else {
            $fb = Bytes $full; $cb = Bytes $compact
            $same = [int]$full.data.layers_profiled -eq [int]$compact.data.layers_profiled
            $detail = "full=$fb B compact=$cb B layers_profiled=$($compact.data.layers_profiled)/$($full.data.layers_profiled)"
            if ($compact.data.response_mode -eq 'compact' -and $cb -lt 20000 -and $same) { Case $names[3] $Q 'pass' $detail }
            else { Case $names[3] $Q 'fail' $detail }
        }
        return (Finish)
    }
}
