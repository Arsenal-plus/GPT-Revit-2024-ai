#Requires -Version 5.1
# Exercises element-kinds-flex.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'element-kinds-flex.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'element-kinds-flex' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

$script:nextId = 2000
$script:deleted = $null
$script:sent = @{}

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $cat = $arguments.categories[0]
        if ($arguments.include_types -eq $true) {
            $rows = switch ($cat) {
                'OST_Sprinklers' { @(@{ element_id = 101; is_element_type = $true; family = 'Sprinkler'; type = 'Upright' }) }
                'OST_PipeCurves' { @(@{ element_id = 102; is_element_type = $true; family = 'Flex Pipe'; type = 'Standard' }) }
                'OST_PipingSystem' { @(@{ element_id = 103; is_element_type = $true; family = 'Piping System'; type = 'Domestic Hot Water' }) }
                'OST_DuctCurves' { @(@{ element_id = 104; is_element_type = $true; family = 'Flex Duct Round' }) }
                'OST_DuctSystem' { @(@{ element_id = 105; is_element_type = $true; family = 'Duct System'; type = 'Supply Air' }) }
                default { @() }
            }
        }
        else {
            $rows = switch ($cat) {
                'OST_Ceilings' { @() }
                'OST_AreaSchemes' { @(@{ element_id = 106; is_element_type = $false }) }
                default { @() }
            }
        }
        return @{ isError = $false; data = [pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) } }
    }
    if ($tool -eq 'horizun_mep_routing') {
        return @{ isError = $false; data = [pscustomobject]@{
            elements = @([pscustomobject]@{ element_id = $script:flexId; size = [pscustomobject]@{ diameter = 50 } })
            duct_sizes = [pscustomobject]@{ round = @(@{ nominal = 50 }, @{ nominal = 65 }) }
        } }
    }
    return @{ isError = $true; text = "unexpected call $tool" }
}
$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $kind = $arguments.elements[0].kind
            $script:nextId++
            $id = $script:nextId
            $row = switch ($kind) {
                'area_boundary' {
                    $ids = @($id, ($id + 1), ($id + 2), ($id + 3))
                    $script:nextId += 3
                    [pscustomobject]@{ element_id = $ids[0]; element_ids = $ids }
                }
                'space' { [pscustomobject]@{ element_id = $id; area_sqft = 0; area_enclosed = $false } }
                'area' { [pscustomobject]@{ element_id = $id; area_sqft = 172.0; area_enclosed = $true } }
                default { [pscustomobject]@{ element_id = $id } }
            }
            if ($kind -eq 'flex_duct') { $script:flexDuctId = $id }
            if ($kind -eq 'flex_pipe') { $script:flexId = $id }
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @($row); postconditions = [pscustomobject]@{ all_verified = $true } } } }
        }
        'horizun_manage_views' {
            $script:nextId++
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } } } }
        }
        'horizun_mep_routing' {
            return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true } } } }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = @{ isError = $false; data = [pscustomobject]@{} } } }
        default { return @{ stage = 'apply'; answer = @{ isError = $true; text = "unexpected apply $tool" } } }
    }
}

$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$cases = @(& $module.Run $ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }

Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($module.Catalog | Where-Object { -not $by.ContainsKey($_.Name) }).Count -eq 0))
Check 'sprinkler placement passes on a verified level-only reply' ($by['sprinkler: place at a point and re-read position, type and level'].Outcome -eq 'pass')
Check 'flex_pipe passes and sends a 3-point path' (($by['flex_pipe: create a path and re-read its points, diameter and level'].Outcome -eq 'pass') -and (@($script:sent[($ctx.RunId + '-ekf-flex-pipe')].elements[0].points).Count -eq 3))
Check 'flex_duct passes on its first (round) attempt' (($by['flex_duct: create a path and re-read its points, size and level'].Outcome -eq 'pass') -and (-not $script:sent.ContainsKey($ctx.RunId + '-ekf-flex-duct-rect')))
Check 'space passes and reports its unbounded area, not a failure' (($by['space: place at a point on a level and re-read the point and level'].Outcome -eq 'pass') -and ($by['space: place at a point on a level and re-read the point and level'].Detail -match 'area_enclosed=False'))
Check 'area_boundary + area passes and reports an enclosed area' (($by['area_boundary + area: close a loop on an area plan view and re-read the area'].Outcome -eq 'pass') -and ($by['area_boundary + area: close a loop on an area plan view and re-read the area'].Detail -match 'area_enclosed=True'))
Check 'area_boundary sent 4 segments closing the loop' (@($script:sent[($ctx.RunId + '-ekf-area-boundary')].elements[0].profile[0]).Count -eq 4)
Check 'mep_routing resize passes both directions' ($by['mep_routing resize: a flex run moves to another catalog size and back, re-read both times'].Outcome -eq 'pass')
Check 'cleanup deletes the level, sprinkler, flex runs, area boundary curves, area and area view' (
    ($script:deleted.Count -ge 10) -and ($script:deleted -contains $script:flexId) -and ($script:deleted -contains $script:flexDuctId)
)

$closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

# ---- a rectangular flex duct: the round attempt is refused, the width/height retry passes ----
$script:sent = @{}
$fakeApplyRect = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    if ($tool -eq 'horizun_create_elements' -and $arguments.elements[0].kind -eq 'flex_duct' -and $arguments.elements[0].ContainsKey('diameter')) {
        return @{ stage = 'apply'; answer = @{ isError = $true; text = 'this duct exposes no settable diameter' } }
    }
    & $fakeApply $tool $arguments $key
}
$ctxRect = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't2'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApplyRect }
$rectCases = @(& $module.Run $ctxRect)
$rectBy = @{}; foreach ($c in $rectCases) { $rectBy[$c.Name] = $c }
Check 'a rectangular flex duct retries with width/height and passes' (
    ($rectBy['flex_duct: create a path and re-read its points, size and level'].Outcome -eq 'pass') -and
    ($script:sent.ContainsKey('t2-ekf-flex-duct-rect')) -and
    ($script:sent['t2-ekf-flex-duct-rect'].elements[0].width -eq 200)
)

if ($fails) { "element-kinds-flex tests: $fails FAILED"; exit 1 } else { 'element-kinds-flex tests: ALL PASS'; exit 0 }
