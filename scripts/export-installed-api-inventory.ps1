[CmdletBinding()]
param(
    [string]$RevitDirectory = 'C:\Program Files\Autodesk\Revit 2024',
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$outDir = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$assemblies = @()
$members = foreach ($assemblyName in @('RevitAPI', 'RevitAPIUI')) {
    $dll = Join-Path $RevitDirectory ($assemblyName + '.dll')
    $xmlPath = Join-Path $RevitDirectory ($assemblyName + '.xml')
    [xml]$documentation = Get-Content -LiteralPath $xmlPath -Raw
    $entries = @($documentation.doc.members.member)
    $assemblies += [ordered]@{
        assembly = $assemblyName
        file_version = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
        dll_sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
        xml_sha256 = (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash
        documented_members = $entries.Count
        documented_types = @($entries | Where-Object name -Like 'T:*').Count
    }
    foreach ($entry in $entries) {
        [pscustomobject]@{
            assembly = $assemblyName
            member = [string]$entry.name
            kind = ([string]$entry.name).Substring(0, 1)
            since = ([string]$entry.since).Trim()
            coverage = 'not_inferred_from_documentation'
        }
    }
}
$csv = Join-Path $outDir 'installed-api-members.csv'
$members | Sort-Object assembly,member | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
[ordered]@{
    generated_utc = [DateTime]::UtcNow.ToString('o')
    source = $RevitDirectory
    assemblies = $assemblies
    inventory_sha256 = (Get-FileHash -LiteralPath $csv -Algorithm SHA256).Hash
    meaning = 'Exact installed XML API inventory. A documented member is not evidence that an MCP tool wraps it or that a scenario has passed. Use horizun_health include_capabilities and live test reports for operation coverage.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outDir 'installed-api-summary.json') -Encoding UTF8
Write-Output "$($members.Count) documented members exported to $csv"
