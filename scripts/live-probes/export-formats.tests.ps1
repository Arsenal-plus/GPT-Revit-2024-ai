#Requires -Version 5.1
# Exercises export-formats.probes.ps1 WITHOUT Revit. The fakes' reply shapes are
# taken from the code (ExportSets.cs: files[].main.header, files_verified,
# read_back, families, saved_in_format) - shapes from the code, to be held against
# the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'export-formats.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'export-formats' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$hasRooms = $true) {
    $state = @{ applies = New-Object System.Collections.Generic.List[string]; nextId = 900 }
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('hz-export-probe-test-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Force -Path $root
    $call = {
        param($tool, $arguments)
        if ($tool -eq 'horizun_query_planimetry') {
            return @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ view_id = 500; view_type = 'FloorPlan'; is_template = $false }) } }
        }
        if ($tool -ne 'horizun_export') { return @{ isError = $true; text = 'unexpected tool ' + $tool } }
        if ($arguments.format -eq 'dwg' -and $arguments.file_naming -eq 'sheet_number') { return @{ isError = $true; text = 'file_naming=sheet_number names sheets only; view 900 is not a sheet. Nothing was exported.' } }
        if ($arguments.format -eq 'gbxml') {
            if (-not $hasRooms) { return @{ isError = $true; text = 'no spaces: the document has no placed, bounded room or space' } }
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; placed_rooms = 3 } }
        }
        if ($arguments.format -eq 'rfa' -and $arguments.category -eq 'OST_Walls') { return @{ isError = $true; text = 'No loadable family to export in category ''OST_Walls''. Nothing was exported.' } }
        if ($arguments.format -eq 'rfa') {
            $fams = @([pscustomobject]@{ id = 71; name = 'Single-Flush' }, [pscustomobject]@{ id = 70; name = 'Double-Glass' })
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; families = $fams } }
        }
        return @{ isError = $true; text = 'unexpected export call' }
    }.GetNewClosure()
    $apply = {
        param($tool, $arguments, $key)
        $state.applies.Add($key)
        $ok = { param($data) @{ stage = 'apply'; answer = @{ isError = $false; data = $data; text = 'ok' } } }
        if ($tool -eq 'horizun_delete_verified') { return (& $ok ([pscustomobject]@{})) }
        if ($tool -eq 'horizun_manage_views') {
            $id = $state.nextId; $state.nextId++
            return (& $ok ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $id; verified = $true }) }))
        }
        $folder = Split-Path $arguments.output_path
        switch ($arguments.format) {
            { $_ -in 'dwg', 'dgn', 'dwfx' } {
                $header = @{ dwg = 'AC1032'; dgn = 'dgn_v8_structured_storage'; dwfx = 'zip_package' }[$arguments.format]
                $files = @($arguments.view_ids | ForEach-Object {
                    $f = Join-Path $folder ("set-$_." + $arguments.format); Set-Content -LiteralPath $f -Value 'x'
                    [pscustomobject]@{ view_id = $_; file = $f; verified = $true; main = [pscustomobject]@{ path = $f; bytes = 1024; header = $header; header_verified = $true }; other_files = @() } })
                return (& $ok ([pscustomobject]@{ format = $arguments.format; files_planned = $files.Count; files_verified = $files.Count; files = $files }))
            }
            'gbxml' { return (& $ok ([pscustomobject]@{ files_verified = 1; placed_rooms = 3; placed_spaces = 0; read_back = [pscustomobject]@{ root = 'gbXML'; space = 3; zone = 1 } })) }
            'rfa' {
                $f = Join-Path $arguments.output_path 'Double-Glass.rfa'; Set-Content -LiteralPath $f -Value 'x'
                return (& $ok ([pscustomobject]@{ files_verified = 1; files = @([pscustomobject]@{ id = 70; file = $f; verified = $true; saved_in_format = '2026'; bytes = 40960 }) }))
            }
        }
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'unexpected apply' } }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $root; RunId = 'r1'; WriteGate = $gate; Call = $call; Apply = $apply; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

$by = Run-Module (New-Ctx $true)
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)

$ctx = New-Ctx $false $true
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
foreach ($n in $catalog) { Check "passes: $n" ($by[$n].Outcome -eq 'pass') }
Check 'the rfa case exports the family first by NAME, not by position' ($by[$catalog[5]].Detail -match 'Double-Glass')
Check 'the probe deletes the views it duplicated' ($ctx.State.applies.Contains('exp-cleanup'))

$by = Run-Module (New-Ctx $false $false)
Check 'no rooms: gbxml passes as a no-spaces refusal' ($by[$catalog[4]].Outcome -eq 'pass' -and $by[$catalog[4]].Detail -match 'no spaces')

if ($fails) { "export-formats probe tests: $fails FAILED"; exit 1 } else { 'export-formats probe tests: ALL PASS'; exit 0 }
