# Shared helper for the probe modules that need a WORKSHARED document to write into
# (groups-worksets, worksets-ownership). Not a probe module itself: the loader only
# dot-sources *.probes.ps1, and each of those dot-sources this file.
#
# The disposable workshared model is the year's closed-workset fixture
# ($Ctx.ClosedWorksetDocument, a central copy next to the write document), opened
# DETACHED preserving worksets: a workshared document nobody else synchronizes to,
# never saved, closed with its changes discarded. MEASURED 2026-09-26 in Revit 2026:
# detach -> 'HZ_CLOSED_L_detached' (IsWorkshared=true), create workset committed and
# verified, open_document on the write model re-activates it, close discard_unsaved
# with a token closes the detached copy.

function Enter-HzWorksharedFixture($Ctx, [string]$Lane) {
    if (-not $Ctx.PSObject.Properties['ClosedWorksetDocument'] -or [string]::IsNullOrWhiteSpace([string]$Ctx.ClosedWorksetDocument)) {
        return @{ Why = 'the run names no -ClosedWorksetDocument, so no disposable workshared fixture can be opened' }
    }
    $h = & $Ctx.Call 'horizun_health' @{}
    $me = @($h.data.open_documents | Where-Object { $_.title -eq $Ctx.Document }) | Select-Object -First 1
    if (-not $me -or -not $me.path -or -not (Test-Path -LiteralPath ([string]$me.path))) {
        return @{ Why = "the write document's path is not readable from health" }
    }
    $writePath = [string]$me.path
    $fixturePath = Join-Path ([IO.Path]::GetDirectoryName($writePath)) ([string]$Ctx.ClosedWorksetDocument + '.rvt')
    if (-not (Test-Path -LiteralPath $fixturePath)) { return @{ Why = "no fixture file at $fixturePath" } }
    $open = & $Ctx.Call 'horizun_document_session' @{
        operation = 'open'; file_path = $fixturePath.Replace([char]92, '/'); detach = $true
        expected_version = [string]$Ctx.Year; idempotency_key = ('ws-fixture-open-' + $Lane + '-' + $Ctx.RunId)
    }
    if ($open.isError -or -not $open.data -or -not $open.data.title) {
        return @{ Why = ('the workshared fixture did not open detached: ' + [string]$open.text); WritePath = $writePath }
    }
    return @{ Title = [string]$open.data.title; WritePath = $writePath; Why = $null }
}

function Exit-HzWorksharedFixture($Ctx, $Fixture, [string]$Lane) {
    if (-not $Fixture -or -not $Fixture.Title) { return 'nothing to close' }
    $back = & $Ctx.Call 'horizun_open_document' @{
        path = ([string]$Fixture.WritePath).Replace([char]92, '/'); activate = $true
        expected_version = [string]$Ctx.Year; idempotency_key = ('ws-fixture-back-' + $Lane + '-' + $Ctx.RunId)
    }
    $dry = & $Ctx.Call 'horizun_document_session' @{ operation = 'close'; target_document = $Fixture.Title; discard_unsaved = $true; dry_run = $true }
    if ($dry.isError -or -not $dry.data.confirmation_token) { return ('close dry run refused: ' + [string]$dry.text) }
    $cl = & $Ctx.Call 'horizun_document_session' @{
        operation = 'close'; target_document = $Fixture.Title; discard_unsaved = $true; dry_run = $false
        confirmation_token = $dry.data.confirmation_token; idempotency_key = ('ws-fixture-close-' + $Lane + '-' + $Ctx.RunId)
    }
    $reactivated = -not $back.isError
    if ($cl.isError -or $cl.data.closed -ne $true) { return ('close failed: ' + [string]$cl.text) }
    return ('fixture closed without saving; write document re-activated=' + $reactivated)
}

# A free host element in the ACTIVE document to move between worksets: the closed-
# workset fixture is an HVAC sample with no walls, so any host model element does.
function Get-HzFreeHostElement($Ctx) {
    foreach ($cat in @('OST_Walls', 'OST_DuctCurves', 'OST_PipeCurves', 'OST_MechanicalEquipment', 'OST_GenericModel')) {
        $r = & $Ctx.Call 'horizun_list_elements' @{ category = $cat; include_links = $false; max_rows = 20 }
        $row = @(@($r.data.rows) | Where-Object { $_.source_kind -eq 'host' }) | Select-Object -First 1
        if ($row) { return [long]$row.element_id }
    }
    return $null
}
