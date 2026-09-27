# Live probes for horizun_export's view sets (dwg/dgn/dwfx, one file per view),
# gbXML and family .rfa (ExportSets.cs). Field ask, 2026-09-26: DWG took exactly
# one view, and gbXML/DGN/DWFX/.rfa had no typed path at all.
#
# Stages two OWN duplicated floor plans (never exports somebody's views by
# position), writes every file under the run's scratch folder, and deletes the
# duplicated views afterwards. Nothing is saved. The .rfa case exports ONE door
# family chosen by name order from the dry run's own list; the model is not
# modified by it (EditFamily works on an in-memory copy).
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'export-formats'
    Catalog = @(
        @{ Name = 'export dwg set: two own views, one verified AC10xx file each'; Tool = 'horizun_export' }
        @{ Name = 'export dwg set: sheet_number naming refuses a view that is not a sheet'; Tool = 'horizun_export' }
        @{ Name = 'export dgn: one own view, header verified'; Tool = 'horizun_export' }
        @{ Name = 'export dwfx: one own view, zip header verified'; Tool = 'horizun_export' }
        @{ Name = 'export gbxml: Space/Zone counts re-read, or no spaces refused'; Tool = 'horizun_export' }
        @{ Name = 'export rfa: one loadable door family, format read back'; Tool = 'horizun_export' }
        @{ Name = 'export rfa: a system category refuses with no loadable family'; Tool = 'horizun_export' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $X = 'horizun_export'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        function Applied($r) { $r.stage -eq 'apply' -and -not $r.answer.isError -and $r.answer.data }
        function Why($r) { "stage=$($r.stage) " + [string]$r.answer.text }
        $doc = $Ctx.Document
        $names = @('export dwg set: two own views, one verified AC10xx file each',
                   'export dwg set: sheet_number naming refuses a view that is not a sheet',
                   'export dgn: one own view, header verified',
                   'export dwfx: one own view, zip header verified',
                   'export gbxml: Space/Zone counts re-read, or no spaces refused',
                   'export rfa: one loadable door family, format read back',
                   'export rfa: a system category refuses with no loadable family')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $X 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }
        $folder = Join-Path $Ctx.ScratchRoot ('hz-export-' + $Ctx.RunId)
        $null = New-Item -ItemType Directory -Force -Path $folder
        $created = New-Object System.Collections.Generic.List[long]

        # ---- own views ----------------------------------------------------------------
        $qv = & $Ctx.Call 'horizun_query_planimetry' @{ mode = 'views'; units = 'mm'; max_rows = 500 }
        $plan = if ($qv.data) { @($qv.data.rows | Where-Object { $_.view_type -eq 'FloorPlan' -and $_.is_template -ne $true })[0] } else { $null }
        $viewIds = @()
        if ($plan) {
            foreach ($suffix in 'A', 'B') {
                $dup = & $Ctx.Apply 'horizun_manage_views' @{ target_document = $doc; actions = @(@{ operation = 'duplicate_view'; source_view_id = [long]$plan.view_id; duplicate_option = 'Duplicate'; name = "HZ_EXP_$suffix`_$($Ctx.RunId)"; key = 'v' }) } "exp-dup-$suffix"
                if (Applied $dup) {
                    $row = @($dup.answer.data.rows) | Select-Object -First 1
                    if ($row.verified -eq $true) { $viewIds += [long]$row.element_id; [void]$created.Add([long]$row.element_id) }
                }
            }
        }
        if ($viewIds.Count -lt 2) {
            foreach ($n in $names[0..3]) { Case $n $X 'not_covered' 'two own floor plans could not be staged' }
        } else {
            # dwg set, view_name naming
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'dwg'; output_path = (Join-Path $folder 'set.dwg'); view_ids = $viewIds; file_naming = 'view_name' } 'exp-dwg-set'
            if (Applied $r) {
                $d = $r.answer.data
                $bad = @($d.files | Where-Object { $_.verified -ne $true -or -not ([string]$_.main.header).StartsWith('AC10') -or -not (Test-Path -LiteralPath $_.file) })
                if ($d.files_verified -eq 2 -and $bad.Count -eq 0) { Case $names[0] $X 'pass' ('headers=' + (@($d.files | ForEach-Object { $_.main.header }) -join ',')) }
                else { Case $names[0] $X 'fail' ($d | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[0] $X 'fail' (Why $r) }

            $r = & $Ctx.Call $X @{ target_document = $doc; format = 'dwg'; output_path = (Join-Path $folder 'sheets.dwg'); view_ids = $viewIds; file_naming = 'sheet_number'; dry_run = $true }
            if ($r.isError -and [string]$r.text -match 'names sheets only') { Case $names[1] $X 'pass' 'refused by name before anything was written' }
            else { Case $names[1] $X 'fail' ('expected a sheets-only refusal: ' + [string]$r.text) }

            foreach ($pair in @(@{ i = 2; f = 'dgn'; want = 'dgn_' }, @{ i = 3; f = 'dwfx'; want = 'zip_package' })) {
                $r = & $Ctx.Apply $X @{ target_document = $doc; format = $pair.f; output_path = (Join-Path $folder ('one.' + $pair.f)); view_ids = @($viewIds[0]) } ('exp-' + $pair.f)
                if (Applied $r) {
                    $row = @($r.answer.data.files)[0]
                    if ($r.answer.data.files_verified -eq 1 -and ([string]$row.main.header).StartsWith($pair.want) -and [long]$row.main.bytes -gt 0) { Case $names[$pair.i] $X 'pass' ("header=$($row.main.header) bytes=$($row.main.bytes)") }
                    else { Case $names[$pair.i] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
                } else { Case $names[$pair.i] $X 'fail' (Why $r) }
            }
        }

        # ---- gbXML --------------------------------------------------------------------
        $gbPath = Join-Path $folder 'energy.xml'
        $pre = & $Ctx.Call $X @{ target_document = $doc; format = 'gbxml'; output_path = $gbPath; dry_run = $true }
        if ($pre.isError -and [string]$pre.text -match 'no spaces') { Case $names[4] $X 'pass' 'no placed room or space: refused as no spaces, nothing written' }
        elseif ($pre.isError) { Case $names[4] $X 'fail' ('dry run refused: ' + [string]$pre.text) }
        else {
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'gbxml'; output_path = $gbPath } 'exp-gbxml'
            if (Applied $r) {
                $rb = $r.answer.data.read_back
                if ($r.answer.data.files_verified -eq 1 -and [int]$rb.space -gt 0 -and $rb.root -eq 'gbXML') { Case $names[4] $X 'pass' ("space=$($rb.space) zone=$($rb.zone) rooms=$($r.answer.data.placed_rooms) spaces=$($r.answer.data.placed_spaces)") }
                else { Case $names[4] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[4] $X 'fail' (Why $r) }
        }

        # ---- rfa ----------------------------------------------------------------------
        $rfaFolder = Join-Path $folder 'families'
        $null = New-Item -ItemType Directory -Force -Path $rfaFolder
        $list = & $Ctx.Call $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; category = 'OST_Doors'; dry_run = $true }
        $door = if ($list.data) { @($list.data.families | Sort-Object { [string]$_.name })[0] } else { $null }
        if (-not $door) { Case $names[5] $X 'not_covered' ('no loadable door family in this document: ' + [string]$list.text) }
        else {
            $r = & $Ctx.Apply $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; family_ids = @([long]$door.id) } 'exp-rfa'
            if (Applied $r) {
                $row = @($r.answer.data.files)[0]
                if ($r.answer.data.files_verified -eq 1 -and $row.verified -eq $true -and [string]$row.saved_in_format -ne '' -and (Test-Path -LiteralPath $row.file)) { Case $names[5] $X 'pass' ("family='$($door.name)' format=$($row.saved_in_format) bytes=$($row.bytes)") }
                else { Case $names[5] $X 'fail' ($r.answer.data | ConvertTo-Json -Compress -Depth 6) }
            } else { Case $names[5] $X 'fail' (Why $r) }
        }
        $r = & $Ctx.Call $X @{ target_document = $doc; format = 'rfa'; output_path = $rfaFolder; category = 'OST_Walls'; dry_run = $true }
        if ($r.isError -and [string]$r.text -match 'No loadable family') { Case $names[6] $X 'pass' 'refused: walls are system families' }
        else { Case $names[6] $X 'fail' ('expected a no-loadable-family refusal: ' + [string]$r.text) }

        if ($created.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = @($created) } 'exp-cleanup' }
        return $cases.ToArray()
    }
}
