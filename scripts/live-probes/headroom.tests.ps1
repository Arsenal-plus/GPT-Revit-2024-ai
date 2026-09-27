#Requires -Version 5.1
# Exercises headroom.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
# The fakes' reply shapes come from the code (CodeCheckHeadroom.cs element rows: outcome, min_clear_mm,
# coverage, measured, on_element, governing.surface.element_id; the view refusal text) - to be held
# against the first live run.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'headroom.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'headroom' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function Reset {
    $script:nextId = 5000; $script:ids = @{}; $script:deleted = $null; $script:copied = $false; $script:copiedName = $null
    $script:firstProfile = $null; $script:alonePasses = $false; $script:upClear = 2850.4
}
Reset
$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = if ($arguments.categories[0] -eq 'OST_Floors' -and $script:copied) { @([pscustomobject]@{ element_id = 31; family = 'Floor'; type = 'Generic 150mm'; is_element_type = $true }) } else { @() }
        return @{ isError = $false; data = [pscustomobject]@{ rows = $rows } }
    }
    if ($tool -eq 'horizun_copy_between_documents') {
        return @{ isError = $true; text = "No type named '__hz_probe_no_such_type__'. Types there: Floor: Concrete-Commercial 362mm | Floor: Generic 150mm | Floor: Generic 300mm." }
    }
    if ($tool -eq 'horizun_code_check' -and $arguments.operation -eq 'headroom') {
        $h = $arguments.headroom
        if ([long]$h.view_id -ne 4000) { return @{ isError = $true; text = "headroom.view_id $($h.view_id) is a Level, not a 3D view: the rays need a View3D." } }
        $id = [long]@($h.element_ids)[0]
        $row = if ($id -eq $script:ids.floorB) {
            [pscustomobject]@{ element_id = $id; outcome = $(if (2850.0 -ge [double]$h.min_mm) { 'passes' } else { 'fails' }); min_clear_mm = 2850.0; coverage = 1.0; measured = 16; on_element = 16
                               governing = [pscustomobject]@{ point_mm = @(1140500.0, 30500.0, 114850.0); surface = [pscustomobject]@{ kind = 'host'; element_id = $script:ids.floorA; category = 'Floors' } } }
        } elseif ($id -eq $script:ids.floorA) {
            [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = $script:upClear; coverage = 1.0; measured = 16; on_element = 16
                               governing = [pscustomobject]@{ point_mm = @(1140500.0, 30500.0, 112000.0); surface = [pscustomobject]@{ kind = 'host'; element_id = $script:ids.floorB; category = 'Floors' } } }
        } elseif ($script:alonePasses) {
            [pscustomobject]@{ element_id = $id; outcome = 'passes'; min_clear_mm = $null; coverage = $null; measured = 0; on_element = 16 }
        } else {
            [pscustomobject]@{ element_id = $id; outcome = 'not_measured'; min_clear_mm = $null; coverage = 0.0; measured = 0; on_element = 16; not_measured = 16
                               reason = 'the rays crossed the element and found no target surface below it (none there, or hidden in this view)' }
        }
        return @{ isError = $false; data = [pscustomobject]@{ operation = 'headroom'; direction = $h.direction; elements = @($row); skipped = @() } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            $el = $arguments.elements[0]
            $script:ids[($key -split '-hdr-')[-1]] = $script:nextId
            if ($el.kind -eq 'floor' -and $null -eq $script:firstProfile) { $script:firstProfile = $el.profile }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }) } } }
        }
        'horizun_copy_between_documents' { $script:copied = $true; $script:copiedName = $arguments.type_names[0]; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        'horizun_manage_views' { return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ aliases = [pscustomobject]@{ hdr = 4000 } } } } }
        'horizun_delete_verified' { $script:deleted = @($arguments.ids); return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
    }
    return @{ stage = 'refused'; answer = @{ isError = $true; text = "unexpected apply $tool" } }
}

$tplDir = Join-Path $env:TEMP ('hz-hdr-tpl-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $tplDir 'English') | Out-Null
Set-Content -LiteralPath (Join-Path $tplDir 'English\DefaultMetric.rte') -Value 'fake'
function Ctx($runId) { [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply; TemplateRoot = $tplDir } }
$names = @($module.Catalog | ForEach-Object { $_.Name })
function Outcome($set, $i) { (@($set | Where-Object { $_.Name -eq $names[$i] })[0]).Outcome }

$cases = @(& $module.Run (Ctx 't1'))
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
Check 'every catalog case is reported once' (($cases.Count -eq $names.Count) -and (@($names | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
Check 'the floor type is copied BY NAME (the first Generic floor, not the first listed)' ($script:copiedName -eq 'Floor: Generic 150mm')
Check 'a floor profile is one loop of four XYZ points' ((@($script:firstProfile).Count -eq 1) -and (@($script:firstProfile[0]).Count -eq 4) -and (@($script:firstProfile[0][0]).Count -eq 3))
Check 'down from B reaches A with full coverage and passes' ((Outcome $cases 0) -eq 'pass')
Check 'up from A matches down within 1 mm' ((Outcome $cases 1) -eq 'pass')
Check 'a threshold above the measured clear height fails the element' ((Outcome $cases 2) -eq 'pass')
Check 'C with nothing below is not_measured' ((Outcome $cases 3) -eq 'pass')
Check 'a level id as view_id is refused by name' ((Outcome $cases 4) -eq 'pass')
Check 'cleanup deletes view, 3 floors, 2 levels and the copied type (7 ids, newest first)' (((Outcome $cases 5) -eq 'pass') -and ($script:deleted.Count -eq 7) -and ($script:deleted[0] -eq 4000) -and ($script:deleted[-1] -eq 31))

Reset; $script:upClear = 2855.0; $script:alonePasses = $true
$bad = @(& $module.Run (Ctx 't2'))
Check 'up and down 5 mm apart fail the symmetry case' ((Outcome $bad 1) -eq 'fail')
Check 'an element with nothing measured reported as passes FAILS the not_measured case' ((Outcome $bad 3) -eq 'fail')

Reset
$none = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't3'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply; TemplateRoot = (Join-Path $env:TEMP 'hz-no-such-template-dir') }
$nt = @(& $module.Run $none)
$m0 = @($nt | Where-Object { $_.Name -eq $names[0] })[0]
Check 'no template: the measuring cases are unverified and name the missing template' (($m0.Outcome -eq 'unverified') -and ($m0.Detail -match 'no Autodesk template') -and ((Outcome $nt 3) -eq 'unverified'))
Check 'no template: the two levels and the view are still deleted (3 ids)' ($script:deleted.Count -eq 3)
Remove-Item -LiteralPath $tplDir -Recurse -Force

$closedCtx = (Ctx 't4'); $closedCtx.WriteGate = $true
$shut = @(& $module.Run $closedCtx)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
if ($fails) { "headroom tests: $fails FAILED"; exit 1 } else { 'headroom tests: ALL PASS'; exit 0 }
