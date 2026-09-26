#Requires -Version 5.1
# Exercises mep-route.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'mep-route.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'mep-route' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$N = @{
    Detour  = 'mep_routing route: a pipe detours around an own column in its straight line, commits, elbows connected, spatial check clean'
    NoRoute = 'mep_routing route: an end inside the column is refused as no_route naming the blocking region'
    Clean   = 'mep-route probes: everything created is deleted'
}

# Reply builders with the ($data, $isError, $text) shape every fake answer shares.
function Reply($data, $isError, $text) { @{ isError = [bool]$isError; data = $data; text = [string]$text } }
function Applied($data, $isError, $text) { @{ stage = 'apply'; answer = (Reply $data $isError $text) } }

$script:nextId = 5000
function Reset-Fake {
    $script:nextId = 5000; $script:deleted = $null; $script:sent = @{}; $script:columnId = $null
    # Default route result: a 4-bend detour, 5 segments, 4 elbows, a clean spatial check.
    $script:routeResult = [pscustomobject]@{
        length_mm = 7400.0; bends = 4
        polyline_mm = @(@(0, 0, 0), @(2400, 0, 0), @(2400, 700, 0), @(3600, 700, 0), @(3600, 0, 0), @(6000, 0, 0))
        segment_ids = @(9001, 9002, 9003, 9004, 9005); elbow_ids = @(9101, 9102, 9103, 9104)
        spatial_check = [pscustomobject]@{ subjects = 9; checked = 9; errors = 0; warnings = 0; links_examined = 0; partial = $false }
    }
    $script:routeState = 'committed_verified'; $script:routeAllVerified = $true
    $script:noRouteText = $null # null = name the column (set after it is created)
}
Reset-Fake

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_PipeCurves' { @(@{ element_id = 201; is_element_type = $true; family = 'Flex Pipe'; type = 'Standard' }, @{ element_id = 202; is_element_type = $true; family = 'Pipe Types'; type = 'Default' }) }
            'OST_PipingSystem' { @(@{ element_id = 203; is_element_type = $true; family = 'Piping System'; type = 'Domestic Cold Water' }) }
            'OST_StructuralColumns' { @(@{ element_id = 204; is_element_type = $true; family = 'Concrete-Rectangular-Column'; type = '300 x 450mm' }) }
            default { @() }
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) }) $false ''
    }
    if ($tool -eq 'horizun_mep_routing') {
        $script:sent['no-route'] = $arguments
        $t = if ($null -ne $script:noRouteText) { $script:noRouteText } else {
            "no_route: the end point is inside host:$($script:columnId):Structural Columns [1..2, 3..4, 5..6] (blocking region: host:$($script:columnId):Structural Columns) Nothing was written."
        }
        return Reply $null $true $t
    }
    return Reply $null $true "unexpected call $tool"
}
$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            if ($arguments.elements[0].kind -eq 'structural_column') { $script:columnId = $script:nextId }
            return Applied ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } }) $false ''
        }
        'horizun_mep_routing' {
            return Applied ([pscustomobject]@{ state = $script:routeState; result = $script:routeResult; postconditions = [pscustomobject]@{ all_verified = $script:routeAllVerified } }) $false ''
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return Applied ([pscustomobject]@{}) $false '' }
        default { return Applied $null $true "unexpected apply $tool" }
    }
}
function Run-Probe($runId) {
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = $runId; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
    $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }
    return $by
}

# ---- the happy path --------------------------------------------------------------------
$by = Run-Probe 't1'
Check 'every catalogued case is reported exactly once' (($by.Count -eq $module.Catalog.Count) -and (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0))
Check 'the detour passes on a committed, verified, clean route' ($by[$N.Detour].Outcome -eq 'pass')
$sentRoute = $script:sent['t1-mrt-route']
Check 'the route request is a straight 6000 mm line through the column, with a rigid pipe type' (
    ($sentRoute.operation -eq 'route') -and ($sentRoute.kind -eq 'pipe') -and ($sentRoute.type_id -eq 202) -and
    ([double]$sentRoute.end[0] - [double]$sentRoute.start[0] -eq 6000) -and ([double]$sentRoute.start[1] -eq [double]$sentRoute.end[1]) -and
    ([double]$script:sent['t1-mrt-column'].elements[0].point[0] -eq ([double]$sentRoute.start[0] + 3000)))
Check 'no_route passes when the refusal names the column and says nothing was written' ($by[$N.NoRoute].Outcome -eq 'pass')
Check 'the no_route request is a dry call (no apply, no dry_run=false)' ((-not $script:sent['no-route'].ContainsKey('dry_run')) -and (-not $script:sent.ContainsKey('t1-mrt-no-route')))
Check 'cleanup deletes the level, the column, every segment and every elbow' (
    ($by[$N.Clean].Outcome -eq 'pass') -and (@($script:deleted).Count -eq 11) -and ($script:deleted -contains $script:columnId) -and
    ($script:deleted -contains 9005) -and ($script:deleted -contains 9104))

# ---- a straight route (no detour) is a fail, not a pass --------------------------------
Reset-Fake
$script:routeResult.bends = 0; $script:routeResult.length_mm = 6000.0
$script:routeResult.segment_ids = @(9001); $script:routeResult.elbow_ids = @()
$by = Run-Probe 't2'
Check 'a route that went straight through the column fails' (($by[$N.Detour].Outcome -eq 'fail') -and ($by[$N.Detour].Detail -match 'did not detour'))

# ---- an elbow missing between two segments fails ---------------------------------------
Reset-Fake
$script:routeResult.elbow_ids = @(9101, 9102, 9103)
$by = Run-Probe 't3'
Check 'a bend without its elbow fails' (($by[$N.Detour].Outcome -eq 'fail') -and ($by[$N.Detour].Detail -match 'elbows'))

# ---- an unverified postcondition is a fail ----------------------------------------------
Reset-Fake
$script:routeAllVerified = $false
$by = Run-Probe 't4'
Check 'a route whose postconditions are not all verified fails' ($by[$N.Detour].Outcome -eq 'fail')

# ---- a spatial error in the result is a fail --------------------------------------------
Reset-Fake
$script:routeResult.spatial_check.errors = 1
$by = Run-Probe 't5'
Check 'a spatial error in the committed result fails' (($by[$N.Detour].Outcome -eq 'fail') -and ($by[$N.Detour].Detail -match 'spatial'))

# ---- a partial spatial check is 'could not look', not clean: a fail ----------------------
Reset-Fake
$script:routeResult.spatial_check.partial = $true
$by = Run-Probe 't5b'
Check 'a partial spatial check in the committed result fails' (($by[$N.Detour].Outcome -eq 'fail') -and ($by[$N.Detour].Detail -match 'spatial'))

# ---- no_route that does not name the column fails ---------------------------------------
Reset-Fake
$script:noRouteText = 'no_route: no path was found within max_nodes Nothing was written.'
$by = Run-Probe 't6'
Check 'a no_route that names no blocking column fails' (($by[$N.NoRoute].Outcome -eq 'fail') -and ($by[$N.NoRoute].Detail -match 'blocking region'))

# ---- a closed write tier ------------------------------------------------------------------
$closed = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't7'; WriteGate = $true; Call = $fakeCall; Apply = $fakeApply }
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

if ($fails) { "mep-route tests: $fails FAILED"; exit 1 } else { 'mep-route tests: ALL PASS'; exit 0 }
