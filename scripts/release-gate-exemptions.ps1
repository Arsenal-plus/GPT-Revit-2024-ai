# Named release-gate exemptions (docs/RELEASE-POLICY.md#named-release-gate-exemption), read
# from release-gate-exemptions.json by every place that judges a release-gate report, so
# one list decides. A report cannot widen it: a not_covered row is exempt only when its name
# is listed here AND its detail starts with the listed reason.
function Get-HzReleaseGateExemptions {
    $path = Join-Path $PSScriptRoot 'release-gate-exemptions.json'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).exemptions)
}

function Test-HzExemptNotCovered($Row, $Exemptions, $Year) {
    if ($Row.outcome -ne 'not_covered') { return $false }
    foreach ($e in @($Exemptions)) {
        if ($null -eq $Year -or @($e.years | ForEach-Object { [int]$_ }) -notcontains [int]$Year) { continue }
        if ([string]$Row.name -ceq [string]$e.case -and ([string]$Row.detail).StartsWith([string]$e.reason_prefix, [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

# The not_covered rows of a report that are NOT named exemptions.
function Get-HzGateNotCovered($Report) {
    $ex = Get-HzReleaseGateExemptions
    return @(@($Report.probes) | Where-Object { $_.outcome -eq 'not_covered' -and -not (Test-HzExemptNotCovered $_ $ex $Report.revit_year) })
}
