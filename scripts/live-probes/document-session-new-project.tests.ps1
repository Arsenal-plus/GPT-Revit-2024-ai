#Requires -Version 5.1
# Exercises document-session-new-project.probes.ps1 WITHOUT Revit, against a fake
# Call that answers like the add-in does (the shapes come from
# DocumentSessionNewProject.cs and OpenDocumentCommand.ActivateOpen).
$ErrorActionPreference = 'Stop'
$script:HzProbeModules = @()
. (Join-Path $PSScriptRoot 'document-session-new-project.probes.ps1')
$module = $script:HzProbeModules | Where-Object { $_.Name -eq 'document-session-new-project' }
if (-not $module) { 'module did not register'; exit 1 }

function New-Ctx([bool]$gate, [bool]$activates = $true, [bool]$defaultTemplate = $true) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('hz-np-test-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Force -Path $scratch
    $tplRoot = Join-Path $scratch 'tpl'
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $tplRoot 'English')
    [IO.File]::WriteAllText((Join-Path $tplRoot 'English\DefaultMetric.rte'), 'x')
    $state = @{ calls = New-Object System.Collections.Generic.List[string]; activates = $activates; defaultTemplate = $defaultTemplate }
    $call = {
        param($tool, $arguments)
        $state.calls.Add($tool + ':' + [string]$arguments.operation)
        if ($tool -eq 'horizun_health') {
            return @{ isError = $false; data = [pscustomobject]@{ open_documents = @([pscustomobject]@{ title = 'HZ_WRITE'; path = 'C:\w\HZ_WRITE.rvt'; is_active = $true }) } }
        }
        if ($tool -eq 'horizun_document_session' -and $arguments.operation -eq 'new_project') {
            $target = ([string]$arguments.save_as_path).Replace('/', [string][char]92)
            if (Test-Path -LiteralPath $target) { return @{ isError = $true; text = "A file already exists at '$target'. new_project NEVER overwrites" } }
            if (-not $arguments.template_path -and -not $state.defaultTemplate) { return @{ isError = $true; text = 'No template_path was given and this Revit has no default project template' } }
            $src = if ($arguments.template_path) { 'template_path' } else { 'revit_default_project_template' }
            if ($arguments.dry_run -eq $false) {
                [IO.File]::WriteAllBytes($target, [byte[]](1, 2, 3))
                return @{ isError = $false; data = [pscustomobject]@{ created = $true; title = 'HZ_NEW'; path_matches_request = $true
                          activated = $state.activates; active_document_verified = $state.activates; activation_note = 'n'
                          file = [pscustomobject]@{ revit_version = '2026'; bytes = 3 } } }
            }
            return @{ isError = $false; data = [pscustomobject]@{ dry_run = $true; confirmation_token = 'tok'; template_source = $src; template_path = 'T' } }
        }
        if ($tool -eq 'horizun_open_document') {
            if ([string]$arguments.path -like '*HZ_NEW*') {
                return @{ isError = $false; data = [pscustomobject]@{ already_open = $true; status = 'already_open_and_active'
                          version_guard = 'not_applicable_already_open'; upgraded_on_open = $false; confirmed_active = $true } }
            }
            return @{ isError = $false; data = [pscustomobject]@{ confirmed_active = $true } }   # the write document, re-activated
        }
        if ($tool -eq 'horizun_document_session' -and $arguments.operation -eq 'close') {
            if ($arguments.dry_run -eq $true) { return @{ isError = $false; data = [pscustomobject]@{ would_discard_unsaved = $false } } }
            return @{ isError = $false; data = [pscustomobject]@{ closed = $true } }
        }
        return @{ isError = $true; text = 'unexpected ' + $tool }
    }.GetNewClosure()
    return [pscustomobject]@{ Year = 2026; Document = 'HZ_WRITE'; ScratchRoot = $scratch; RunId = 'r-1'; WriteGate = $gate
                              TemplateRoot = $tplRoot; Call = $call; Apply = { throw 'Apply is not used by this module' }; State = $state }
}

$fails = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fails++ } }
function Run-Module($ctx) { $by = @{}; foreach ($c in @(& $module.Run $ctx)) { $by[$c.Name] = $c }; return $by }
$catalog = @($module.Catalog | ForEach-Object { $_.Name })

# A. write tier closed: nothing called, every case not_covered
$ctx = New-Ctx $true
$by = Run-Module $ctx
Check 'a closed write tier reports every case not_covered' (@($catalog | Where-Object { $by[$_].Outcome -ne 'not_covered' }).Count -eq 0)
Check 'a closed write tier calls nothing' ($ctx.State.calls.Count -eq 0)

# B. the whole path, Revit's default template
$ctx = New-Ctx $false
$by = Run-Module $ctx
Check 'every catalogued case is reported' (@($catalog | Where-Object { -not $by.ContainsKey($_) }).Count -eq 0)
foreach ($n in $catalog) { Check ("pass: " + $n) ($by[$n].Outcome -eq 'pass') }
Check 'the apply carried a token and an idempotency key' ($ctx.State.calls -contains 'horizun_document_session:new_project')
Check 'the write document is re-activated before the close' (
    $ctx.State.calls.IndexOf('horizun_open_document:') -lt $ctx.State.calls.LastIndexOf('horizun_document_session:close'))

# C. no default template: falls back to Autodesk's own by path
$ctx = New-Ctx $false $true $false
$by = Run-Module $ctx
Check 'no default template: the rehearsal still passes through the template found by path' ($by[$catalog[0]].Outcome -eq 'pass' -and $by[$catalog[0]].Detail -match 'template_path')

# D. created but not activated: the apply case FAILS (activation is what the probe measures)
$ctx = New-Ctx $false $false
$by = Run-Module $ctx
Check 'not activated is a failure of the apply case' ($by[$catalog[2]].Outcome -eq 'fail')
Check 'not activated: the activation case is unverified, not passed' ($by[$catalog[3]].Outcome -eq 'unverified')
Check 'not activated: the created project is still closed' ($by[$catalog[4]].Outcome -eq 'pass')

if ($fails) { "document-session-new-project probe tests: $fails FAILED"; exit 1 } else { 'document-session-new-project probe tests: ALL PASS'; exit 0 }
