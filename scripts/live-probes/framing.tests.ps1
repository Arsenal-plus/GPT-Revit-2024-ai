#Requires -Version 5.1
# Exercises framing.probes.ps1 WITHOUT Revit: its Run against fake Call/Apply whose replies
# copy the shapes FramingApply.cs / FramingRead.cs build (plan.sources[].count_by_role,
# evidence.sources[], already_applied, evidence.removed_ids, member_count). Those shapes are
# the code's, not yet measured live: the first live run must compare them.
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'framing.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'framing' }
$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Reply($data, $isError, $text) { @{ isError = $isError; data = $data; text = $text } }

# The probe finds the member template under ProgramData; give it an empty stand-in.
$fakeData = Join-Path ([IO.Path]::GetTempPath()) ('hz-framing-tests-' + [guid]::NewGuid().ToString('N'))
$tplDir = Join-Path $fakeData 'Autodesk\RVT 2026\Family Templates\English'
New-Item -ItemType Directory -Force -Path $tplDir | Out-Null
Set-Content -LiteralPath (Join-Path $tplDir 'Metric Generic Model line based.rft') -Value ''
$realProgramData = $env:ProgramData
$env:ProgramData = $fakeData

function New-State { $script:nextId = 7000; $script:applies = 0; $script:removed = $false; $script:deleted = $null; $script:sent = @{} }

$fakeCall = {
    param($tool, $arguments)
    if ($tool -eq 'horizun_query_model') {
        $rows = switch ($arguments.categories[0]) {
            'OST_Walls' { @(@{ element_id = 401; is_element_type = $true; family = 'Basic Wall'; type = 'Generic - 200mm' }) }
            'OST_Doors' { @(@{ element_id = 402; is_element_type = $true; family = 'M_Single-Flush'; type = '0915 x 2134mm' }) }
            'OST_Windows' { @(@{ element_id = 403; is_element_type = $true; family = 'M_Fixed'; type = '0915 x 1220mm' }) }
            default { @() }
        }
        return Reply ([pscustomobject]@{ rows = @($rows | ForEach-Object { [pscustomobject]$_ }) }) $false ''
    }
    if ($tool -eq 'horizun_framing') {
        switch ($arguments.operation) {
            'wall' {
                return Reply ([pscustomobject]@{ dry_run = $true; operation = 'wall'; transaction_status = 'not_started'
                    plan = [pscustomobject]@{ member_count = 30; sources = @([pscustomobject]@{ source_id = 7003; status = 'planned'; member_count = 30
                        count_by_role = [pscustomobject]@{ cripple = 3; header = 2; jack = 4; king = 4; sill = 1; stud = 14; track = 2 }
                        openings = @([pscustomobject]@{ id = '7004'; read_from = 'rough' }, [pscustomobject]@{ id = '7005'; read_from = 'nominal' }) }) } }) $false ''
            }
            'read' {
                $n = 30; if ($script:removed) { $n = 0 }
                return Reply ([pscustomobject]@{ operation = 'read'; member_count = $n; sources = @() }) $false ''
            }
            'ceiling' { return Reply $null $true 'horizun_framing operation=ceiling is not available in this build yet. Nothing was read or written.' }
        }
    }
    return Reply $null $true "unexpected call $tool"
}

$script:wallApply = {
    param($arguments)
    $script:applies++
    Reply ([pscustomobject]@{ dry_run = $false; operation = 'wall'; transaction_status = 'Committed'; already_applied = ($script:applies -gt 1)
        postconditions = [pscustomobject]@{ all_verified = $true }
        evidence = [pscustomobject]@{ endpoint_read = @('location_curve'); sources = @([pscustomobject]@{ source_id = 7003; already_applied = ($script:applies -gt 1)
            planned = 30; found = 30; max_endpoint_deviation_mm = 0.0; stud_crossings = 0; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
}

$fakeApply = {
    param($tool, $arguments, $key)
    $script:sent[$key] = $arguments
    switch ($tool) {
        'horizun_create_family' { return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ loaded_family = [pscustomobject]@{ symbol_ids = @(6001) } }) $false '') } }
        'horizun_create_elements' {
            $script:nextId++
            return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ rows = @([pscustomobject]@{ element_id = $script:nextId }); postconditions = [pscustomobject]@{ all_verified = $true } }) $false '') }
        }
        'horizun_framing' {
            if ($arguments.operation -eq 'remove') {
                $script:removed = $true
                return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{ operation = 'remove'; postconditions = [pscustomobject]@{ all_verified = $true }
                    evidence = [pscustomobject]@{ removed_ids = @(1..30 | ForEach-Object { 8000 + $_ }) } }) $false '') }
            }
            return @{ stage = 'apply'; answer = (& $script:wallApply $arguments) }
        }
        'horizun_delete_verified' { $script:deleted = $arguments.ids; return @{ stage = 'apply'; answer = (Reply ([pscustomobject]@{}) $false '') } }
        default { return @{ stage = 'apply'; answer = (Reply $null $true "unexpected apply $tool") } }
    }
}

try {
    New-State
    $ctx = [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = (Join-Path $fakeData 'scratch'); RunId = 't1'; WriteGate = $false; Call = $fakeCall; Apply = $fakeApply }
    $cases = @(& $module.Run $ctx)
    $by = @{}; foreach ($c in $cases) { $by[$c.Name] = $c }
    $n = $module.Catalog | ForEach-Object { $_.Name }
    Check 'every catalogued case is reported exactly once' (($cases.Count -eq $module.Catalog.Count) -and (@($n | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0))
    Check 'the rehearsal with two openings and every role passes' ($by[$n[0]].Outcome -eq 'pass')
    Check 'the verified apply passes' ($by[$n[1]].Outcome -eq 'pass')
    Check 'the second apply is already_applied' ($by[$n[2]].Outcome -eq 'pass')
    Check 'read and remove pass' (($by[$n[3]].Outcome -eq 'pass') -and ($by[$n[4]].Outcome -eq 'pass'))
    Check 'the ceiling cases are not_covered with the reason' (($by[$n[5]].Outcome -eq 'not_covered') -and ($by[$n[5]].Detail -match 'refused by name') -and ($by[$n[6]].Outcome -eq 'not_covered'))
    $applySent = $script:sent['t1-fr-apply']   # not $sent: that IS $script:sent here
    Check 'the apply names the wall and the authored member type everywhere' (($applySent.operation -eq 'wall') -and ($applySent.element_ids[0] -eq 7002) -and ($applySent.spec.wall.stud.type_id -eq 6001) -and ($applySent.spec.wall.track.bottom_type_id -eq 6001))
    $hosted = @($script:sent.Values | Where-Object { $_.elements -and @($_.elements)[0].kind -eq 'family_instance' -and @($_.elements)[0].host_id -eq 7002 }).Count
    Check 'the door and the window are hosted on the staged wall' ($hosted -eq 2)
    Check 'cleanup deletes level, wall, door and window' (($by[$n[7]].Outcome -eq 'pass') -and ($script:deleted.Count -eq 4))

    # ---- a stud through an opening is a fail, not a pass ----
    New-State
    $script:wallApply = {
        param($arguments)
        $script:applies++
        Reply ([pscustomobject]@{ already_applied = $false; postconditions = [pscustomobject]@{ all_verified = $true }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 30; stud_crossings = 1; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
    }
    $bad = @(& $module.Run $ctx); $badBy = @{}; foreach ($c in $bad) { $badBy[$c.Name] = $c }
    Check 'a stud crossing an opening fails the apply case with the count named' (($badBy[$n[1]].Outcome -eq 'fail') -and ($badBy[$n[1]].Detail -match 'crossings 1'))
    Check 'nothing downstream claims a pass after a failed apply' (($badBy[$n[2]].Outcome -eq 'not_covered') -and ($badBy[$n[4]].Outcome -eq 'not_covered'))

    # ---- a second apply that builds again is a fail ----
    New-State
    $script:wallApply = {
        param($arguments)
        $script:applies++
        Reply ([pscustomobject]@{ already_applied = $false; postconditions = [pscustomobject]@{ all_verified = $true }
            evidence = [pscustomobject]@{ sources = @([pscustomobject]@{ found = 30; stud_crossings = 0; inserts_checked = 2; inserts_changed = 0 }) } }) $false ''
    }
    $dbl = @(& $module.Run $ctx); $dblBy = @{}; foreach ($c in $dbl) { $dblBy[$c.Name] = $c }
    Check 'a second apply that is not already_applied fails' ($dblBy[$n[2]].Outcome -eq 'fail')

    $closed = $ctx.PSObject.Copy(); $closed.WriteGate = $true
    $shut = @(& $module.Run $closed)
    Check 'a closed write tier reports every case not_covered' ((@($shut | Where-Object { $_.Outcome -eq 'not_covered' }).Count -eq $module.Catalog.Count))
}
finally {
    $env:ProgramData = $realProgramData
    Remove-Item -LiteralPath $fakeData -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fails) { "framing tests: $fails FAILED"; exit 1 } else { 'framing tests: ALL PASS'; exit 0 }
