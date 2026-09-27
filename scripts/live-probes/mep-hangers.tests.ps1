#Requires -Version 5.1
# Exercises mep-hangers.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'mep-hangers.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'mep-hangers' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }

function New-State { $script:nextId = 5000; $script:deleted = $null; $script:sent = @{}; $script:floorId = $null }
New-State

# A fake reply in the bridge's shape: ($data, $isError, $text).
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }

# One fake Call, no closures (GetNewClosure would hide this script's functions from it):
# the scenario is chosen through $script:genericTypes and $script:bareReply.
$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_PipeCurves' { @(@{ element_id = 201; is_element_type = $true; family = 'Pipe Types'; type = 'Standard' }) }
            'OST_PipingSystem' { @(@{ element_id = 202; is_element_type = $true; family = 'Piping System'; type = 'Domestic Cold Water' }) }
            'OST_Floors' { @(@{ element_id = 203; is_element_type = $true; family = 'Floor'; type = 'Generic 300mm' }) }
            'OST_GenericModel' { $script:genericTypes }
            default { @() }
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) }) $false ''
    }
    if ($tool -eq 'horizun_mep_routing') { return & $script:bareReply }
    return Reply $null $true "unexpected call $tool"
}

function Placed($floor, $along, $rod) {
    $i = 0
    return @($along | ForEach-Object { $i++; [pscustomobject]@{ element_id = 9000 + $i; along_mm = $_; rod_mm = $rod
        support = [pscustomobject]@{ source = 'host'; element_id = $floor } } })
}

$goodAlong = @(300, 1650, 3000, 4350, 5700)
$script:hangerReply = { param($arguments) Reply ([pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
    result = [pscustomobject]@{ placed = (Placed $script:floorId $goodAlong 1725.0) } }) $false '' }

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_elements' {
            $script:nextId++
            if ($arguments.elements[0].kind -eq 'floor') { $script:floorId = $script:nextId }
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } }) $false '') }
        }
        'horizun_mep_routing' { return @{ stage = 'apply'; answer = (& $script:hangerReply $arguments) } }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        'horizun_create_family' {
            # The REAL shape (MEASURED 2026-09-26): loaded_family.symbol_ids after load_into_project.
            if ($script:authorSymbol) { return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ loaded_family = [pscustomobject]@{ symbol_ids = @($script:authorSymbol) } }) $false '') } }
            return @{ stage = 'apply'; answer = (Reply $null $true 'no template in the fake') }
        }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

$generic = @(@{ element_id = 301; is_element_type = $true; family = 'Generic Model'; type = 'Type 1' })
$refusedBare = { Reply $null $true 'no station can carry a hanger: 0 run(s) shorter than 2 x end_offset_mm, 5 station(s) with no floor, framing or roof above within 5000 mm. Nothing was written.' }
$script:genericTypes = $generic; $script:bareReply = $refusedBare
$ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$cases = @(& $module.Run $ctx)
$by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
$n = $module.Catalog | ForEach-Object { $_.Name }

Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
Check 'five evenly spaced hangers under the own floor pass' ($by[$n[0]].Outcome -eq 'pass')
$sentHangers = $script:sent['t1-hg-apply-301']
Check 'the apply names the pipe, the type, spacing and end offset' (($sentHangers.operation -eq 'hangers') -and ($sentHangers.hanger_type_id -eq 301) -and ($sentHangers.spacing_mm -eq 1500) -and ($sentHangers.end_offset_mm -eq 300))
Check 'the floor is staged on the upper level, above the pipe' (($script:sent['t1-hg-floor'].elements[0].profile[0][0][2] -gt $script:sent['t1-hg-pipe'].elements[0].start[2]))
Check 'the bare run is refused as no support' ($by[$n[1]].Outcome -eq 'pass')
Check 'cleanup deletes levels, floor, pipes and the 5 hangers' (($script:deleted.Count -eq 10) -and ($script:deleted -contains 9001) -and ($script:deleted -contains $script:floorId))

# ---- a wrong count or a gap above spacing is a fail, not a pass ----
New-State
$script:hangerReply = { param($arguments) Reply ([pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
    result = [pscustomobject]@{ placed = (Placed $script:floorId @(300, 3000, 5700) 1725.0) } }) $false '' }
$bad = @(& $module.Run $ctx); $badBy = @{}; foreach ($c in $bad) { $badBy[$c.Name] = $c }
Check 'three hangers with 2700 mm gaps fail with the count and the gap named' (($badBy[$n[0]].Outcome -eq 'fail') -and ($badBy[$n[0]].Detail -match 'expected 5') -and ($badBy[$n[0]].Detail -match 'gap exceeds'))

# ---- a rod measured to the floor's TOP face (through the slab) is a fail ----
New-State
$script:hangerReply = { param($arguments) Reply ([pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
    result = [pscustomobject]@{ placed = (Placed $script:floorId $goodAlong 1970.0) } }) $false '' }
$top = @(& $module.Run $ctx); $topBy = @{}; foreach ($c in $top) { $topBy[$c.Name] = $c }
Check 'a rod to the top face of the floor fails with the rod named' (($topBy[$n[0]].Outcome -eq 'fail') -and ($topBy[$n[0]].Detail -match "underside"))

# ---- a reply naming a gap above spacing is a fail ----
New-State
$script:hangerReply = { param($arguments) Reply ([pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
    result = [pscustomobject]@{ placed = (Placed $script:floorId $goodAlong 1725.0); gaps_above_spacing = 1 } }) $false '' }
$gp = @(& $module.Run $ctx); $gpBy = @{}; foreach ($c in $gp) { $gpBy[$c.Name] = $c }
Check 'a reply naming a gap above spacing fails' (($gpBy[$n[0]].Outcome -eq 'fail') -and ($gpBy[$n[0]].Detail -match 'gap'))

# ---- a bare run that PLANS stations is a fail ----
New-State
$script:hangerReply = { param($arguments) Reply ([pscustomobject]@{ state = 'committed_verified'; postconditions = [pscustomobject]@{ all_verified = $true }
    result = [pscustomobject]@{ placed = (Placed $script:floorId $goodAlong 1725.0) } }) $false '' }
$plannedBare = { Reply ([pscustomobject]@{ plan = [pscustomobject]@{ planned = 5 } }) $false '' }
$script:bareReply = $plannedBare
$ctxPlan = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't2'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$pl = @(& $module.Run $ctxPlan); $plBy = @{}; foreach ($c in $pl) { $plBy[$c.Name] = $c }
Check 'a bare run that plans hangers fails' ($plBy[$n[1]].Outcome -eq 'fail')

# ---- no generic model type: not_covered with the reason ----
New-State
$script:genericTypes = @(); $script:bareReply = $refusedBare
$ctxNone = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't3'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
$none = @(& $module.Run $ctxNone); $noneBy = @{}; foreach ($c in $none) { $noneBy[$c.Name] = $c }
Check 'no generic model type reports not_covered with the reason' (($noneBy[$n[0]].Outcome -eq 'not_covered') -and ($noneBy[$n[0]].Detail -match 'generic model') -and ($noneBy[$n[1]].Outcome -eq 'not_covered'))

# ---- the probe's OWN hanger comes first, even when the fixture carries generic models ----
# MEASURED 2026-09-26: the 2023 fixture's first generic model is a balcony the tool placed at
# z=0; the case must measure the operation on a hanger it authored, not a stranger's family.
New-State
$script:genericTypes = $generic; $script:bareReply = $refusedBare; $script:authorSymbol = 777
$fakeProgramData = Join-Path $env:TEMP ('hz-hg-pd-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $fakeProgramData 'Autodesk\RVT 2026\Family Templates\English') | Out-Null
Set-Content -LiteralPath (Join-Path $fakeProgramData 'Autodesk\RVT 2026\Family Templates\English\Metric Generic Model.rft') -Value 'fake'
$realProgramData = $env:ProgramData
try {
    $env:ProgramData = $fakeProgramData
    $own = @(& $module.Run ([pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $env:TEMP; RunId = 't4'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }))
} finally { $env:ProgramData = $realProgramData; $script:authorSymbol = $null }
$ownSent = $script:sent['t4-hg-apply-777']
Check 'the authored hanger is used before the fixture''s generic models, with its rod parameter' (
    $ownSent -and ($ownSent.hanger_type_id -eq 777) -and ($ownSent.rod_length_parameter -eq 'HZ Rod Length') -and (-not $script:sent.ContainsKey('t4-hg-apply-301')))

$closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
$shut = @(& $module.Run $closed)
Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))

if ($fails) { "mep-hangers tests: $fails FAILED"; exit 1 } else { 'mep-hangers tests: ALL PASS'; exit 0 }
