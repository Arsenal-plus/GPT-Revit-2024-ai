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
    # The release gate names the write model itself as this fixture: opening its path returns
    # the document already open, not a detached copy - and a copy of that central cannot be
    # opened while the central is (Revit: 'Cannot open the local model and the central model
    # in the same Revit session', MEASURED 2026-09-27). Callers that need a DETACHED document
    # compare the returned title with the write document's.
    return (Enter-HzFixtureFile $Ctx $fixturePath $Lane $writePath)
}

# Open any fixture FILE detached (a central keeps its worksets; a plain model just opens)
# and make it the active document. Never saved: Exit-HzWorksharedFixture closes it with
# its changes discarded and re-activates the write document.
function Enter-HzFixtureFile($Ctx, [string]$FixturePath, [string]$Lane, [string]$WritePath) {
    if (-not $WritePath) {
        $h = & $Ctx.Call 'horizun_health' @{}
        $me = @($h.data.open_documents | Where-Object { $_.title -eq $Ctx.Document }) | Select-Object -First 1
        if (-not $me -or -not $me.path) { return @{ Why = "the write document's path is not readable from health" } }
        $WritePath = [string]$me.path
    }
    if (-not (Test-Path -LiteralPath $FixturePath)) { return @{ Why = "no fixture file at $FixturePath" } }
    $open = & $Ctx.Call 'horizun_document_session' @{
        operation = 'open'; file_path = $FixturePath.Replace([char]92, '/'); detach = $true
        expected_version = [string]$Ctx.Year; idempotency_key = ('fixture-open-' + $Lane + '-' + $Ctx.RunId)
    }
    if ($open.isError -or -not $open.data -or -not $open.data.title) {
        return @{ Why = ('the fixture did not open detached: ' + [string]$open.text); WritePath = $WritePath }
    }
    return @{ Title = [string]$open.data.title; WritePath = $WritePath; Why = $null }
}

function Exit-HzWorksharedFixture($Ctx, $Fixture, [string]$Lane) {
    if (-not $Fixture -or -not $Fixture.Title) { return 'nothing to close' }
    $back = & $Ctx.Call 'horizun_open_document' @{
        path = ([string]$Fixture.WritePath).Replace([char]92, '/'); activate = $true
        expected_version = [string]$Ctx.Year; idempotency_key = ('fixture-back-' + $Lane + '-' + $Ctx.RunId)
    }
    if ($back.isError) {
        # The release gate's write model is a CENTRAL opened with open_central, and
        # open_document refuses a central (MEASURED 2026-09-27, v2.1.2 gate): the fixture
        # stayed active and every later probe refused the active-document check.
        # document_session open over an already-open document only activates it.
        $back = & $Ctx.Call 'horizun_document_session' @{
            operation = 'open'; file_path = ([string]$Fixture.WritePath).Replace([char]92, '/'); open_central = $true
            expected_version = [string]$Ctx.Year; idempotency_key = ('fixture-back-central-' + $Lane + '-' + $Ctx.RunId)
        }
    }
    $dry =& $Ctx.Call 'horizun_document_session' @{ operation = 'close'; target_document = $Fixture.Title; discard_unsaved = $true; dry_run = $true }
    if ($dry.isError -or -not $dry.data) { return ('close dry run refused: ' + [string]$dry.text) }
    # A CLOSE THAT DISCARDS NOTHING NEEDS NO TOKEN. MEASURED 2026-09-26: an unmodified
    # document (a sample opened only to be read) rehearses with would_discard_unsaved=false
    # and no confirmation_token - "call again with dry_run=false". Treating the missing
    # token as a refusal left the sample open with its six links, and the matrix driver
    # then refused to close a Revit holding documents it had not opened.
    $closeArgs = @{
        operation = 'close'; target_document = $Fixture.Title; discard_unsaved = $true; dry_run = $false
        idempotency_key = ('fixture-close-' + $Lane + '-' + $Ctx.RunId)
    }
    if ($dry.data.confirmation_token) { $closeArgs['confirmation_token'] = $dry.data.confirmation_token }
    elseif ($dry.data.would_discard_unsaved -ne $false) { return ('close dry run issued no token for a close that would discard changes: ' + [string]$dry.text) }
    $cl = & $Ctx.Call 'horizun_document_session' $closeArgs
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
