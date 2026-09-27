# Live probes for horizun_copy_between_documents source_path (a library
# .rvt/.rte NOT already open, opened in the BACKGROUND, never activated, and
# closed WITHOUT SAVING before the call returns).
#
# Two cases need no fixture at all - the mutual-exclusivity refusal and the
# not-found refusal both fire before any file is touched. The third, the real
# open+copy+close, needs a small library file with at least one named type -
# read from %USERPROFILE%\.horizun\live-fixtures.json, keys LibraryDocument
# (a .rvt/.rte path) and LibraryDocumentTypeName (a type name known to be in
# it; LibraryDocumentCategory is optional, a BuiltInCategory token to narrow
# the match). Missing either key reports not_covered with the reason, the same
# pattern -ClosedWorksetDocument/-ClosedWorksetName already use in
# verify-live.ps1 for their own still-pending fixture.
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'copy-between-documents-source-path'
    Catalog = @(
        @{ Name = 'copy_between_documents: source_document and source_path together are refused'; Tool = 'horizun_copy_between_documents' }
        @{ Name = 'copy_between_documents: a source_path that does not exist is refused before opening'; Tool = 'horizun_copy_between_documents' }
        @{ Name = 'copy_between_documents: source_path opens a library in the background, copies a type by name, and closes it without saving'; Tool = 'horizun_copy_between_documents' }
    )
    Run     = {
        param($Ctx)
        $cases = New-Object System.Collections.Generic.List[object]
        $C = 'horizun_copy_between_documents'
        function Case($name, $tool, $outcome, $detail) { $cases.Add(@{ Name = $name; Tool = $tool; Outcome = $outcome; Detail = [string]$detail }) }
        $doc = $Ctx.Document
        $names = @('copy_between_documents: source_document and source_path together are refused',
                   'copy_between_documents: a source_path that does not exist is refused before opening',
                   'copy_between_documents: source_path opens a library in the background, copies a type by name, and closes it without saving')

        if ($Ctx.WriteGate) {
            foreach ($n in $names) { Case $n $C 'not_covered' 'write tier is not open for this run' }
            return $cases.ToArray()
        }

        # ---- A: mutual exclusivity, no file touched --------------------------------
        $both = & $Ctx.Call $C @{ target_document = $doc; source_document = 'not open anywhere'; source_path = 'C:\nowhere.rvt'; element_ids = @(1) }
        if ($both.isError -and [string]$both.text -match 'exactly one of source_document') { Case $names[0] $C 'pass' 'refused: exactly one of source_document/source_path' }
        else { Case $names[0] $C 'fail' ('expected the mutual-exclusivity refusal: ' + $both.text) }

        # ---- B: not found, before any open ------------------------------------------
        $missingPath = Join-Path $Ctx.ScratchRoot ('HZ_PROBE_NO_SUCH_' + $Ctx.RunId + '.rvt')
        $nf = & $Ctx.Call $C @{ target_document = $doc; source_path = $missingPath; type_names = @('anything') }
        if ($nf.isError -and [string]$nf.text -match 'File not found') { Case $names[1] $C 'pass' 'refused: File not found, before opening' }
        else { Case $names[1] $C 'fail' ('expected a not-found refusal: ' + $nf.text) }

        # ---- C: the real open+copy+close --------------------------------------------
        $fixturesPath = Join-Path $env:USERPROFILE '.horizun\live-fixtures.json'
        $lib = $null; $typeName = $null; $category = $null
        if (Test-Path -LiteralPath $fixturesPath) {
            try {
                $fx = Get-Content -LiteralPath $fixturesPath -Raw | ConvertFrom-Json
                if ($fx.LibraryDocument) { $lib = [string]$fx.LibraryDocument }
                if ($fx.LibraryDocumentTypeName) { $typeName = [string]$fx.LibraryDocumentTypeName }
                if ($fx.LibraryDocumentCategory) { $category = [string]$fx.LibraryDocumentCategory }
            } catch { }
        }
        # One fixture line serves every year: a '{year}' in the path becomes the run's
        # Revit year, so an Autodesk template of THAT year is the library (a newer file
        # cannot be opened by an older Revit at all).
        if ($lib -and $Ctx.Year) { $lib = $lib.Replace('{year}', [string]$Ctx.Year) }
        if (-not $lib -or -not (Test-Path -LiteralPath $lib) -or -not $typeName) {
            Case $names[2] $C 'not_covered' ('needs -Fixtures LibraryDocument (a .rvt/.rte path) and LibraryDocumentTypeName (a type name known ' +
                'to be in it) in live-fixtures.json; LibraryDocument=' + [bool]$lib + ' exists=' + [bool]($lib -and (Test-Path -LiteralPath $lib)) +
                ' LibraryDocumentTypeName=' + [bool]$typeName)
            return $cases.ToArray()
        }

        # use_destination: a write model that already holds the family's patterns or
        # materials must not turn this case into a collision refusal (measured 2026-09-26).
        $req = @{ target_document = $doc; source_path = $lib; type_names = @($typeName); duplicate_types = 'use_destination' }
        if ($category) { $req.category = $category }
        # NOT `$c`: PowerShell names are case-insensitive, so `$c` IS `$C` and the tool name would
        # become this reply - every case after it then reports a Hashtable as its tool.
        $copy = & $Ctx.Apply $C $req 'cbd-source-path'
        $ok = $copy.stage -eq 'apply' -and -not $copy.answer.isError -and $copy.answer.data -and $copy.answer.data.host_verified -eq $true -and
              $copy.answer.data.source_open -and $copy.answer.data.source_open.opened_in_background -eq $true -and
              $copy.answer.data.source_open.will_be_closed_without_saving -eq $true -and @($copy.answer.data.rows).Count -gt 0
        if ($ok) {
            $createdIds = @(@($copy.answer.data.rows) | ForEach-Object { [long]$_.element_id })
            Case $names[2] $C 'pass' ('copied ' + $createdIds.Count + ' element(s) from ' + $lib + ', types_that_arrived=' + @($copy.answer.data.types_that_arrived).Count)
            if ($createdIds.Count -gt 0) { $null = & $Ctx.Apply 'horizun_delete_verified' @{ target_document = $doc; mode = 'ids'; ids = $createdIds } 'cbd-cleanup' }
        }
        else { Case $names[2] $C 'fail' ("stage=$($copy.stage) " + [string]$copy.answer.text) }

        return $cases.ToArray()
    }
}
