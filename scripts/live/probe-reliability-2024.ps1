param([Parameter(Mandatory)][string]$Server, [Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$call = Join-Path $PSScriptRoot '..\hz-call.ps1'
function Invoke-Probe([string]$Tool, [hashtable]$Arguments, [string]$Name, [switch]$ExpectError) {
    $path = Join-Path $RunRoot ($Name + '.json')
    & $call -Server $Server -Tool $Tool -ArgumentsObject $Arguments -Json $path -Quiet -TimeoutSec 180
    $reply = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (!$reply.replied -or ([bool]$reply.is_error -ne [bool]$ExpectError)) { throw "Unexpected result in $Name; inspect $path" }
    return $reply
}
$target = Join-Path $RunRoot 'electrical.rvt'
$name = 'MCP Workflow ' + [Guid]::NewGuid().ToString('N')
$argsPlan = @{
    target_document=$target
    steps=@(
        @{ key='switch'; tool='horizun_document_session'; arguments=@{ operation='open'; file_path=(Join-Path $RunRoot 'metric.rvt') } },
        @{ key='make'; tool='horizun_create_elements'; arguments=@{ units='feet'; elements=@(@{kind='level'; name=$name; elevation=170}) } },
        @{ key='read'; tool='horizun_model_snapshot'; arguments=@{ element_ids=@('${make.rows.0.element_id}') } },
        @{ key='switch_end'; tool='horizun_document_session'; arguments=@{ operation='open'; file_path=(Join-Path $RunRoot 'metric.rvt') } }
    )
}
$preview = Invoke-Probe 'horizun_run_workflow' $argsPlan 'workflow-preview'
$argsPlan.confirmation_token = $preview.result.confirmation_token
$argsPlan.idempotency_key = 'audit-workflow-' + [Guid]::NewGuid().ToString('N')
$argsPlan.dry_run = $false
$applied = Invoke-Probe 'horizun_run_workflow' $argsPlan 'workflow-apply'
if ($applied.result.workflow.state -ne 'verified_completed' -or !$applied.result.results.read.complete) { throw 'Workflow was not verified.' }
$replayed = Invoke-Probe 'horizun_run_workflow' $argsPlan 'workflow-replay'
if ($replayed.result.idempotency.status -ne 'replayed' -or
    $replayed.result.results.make.rows[0].element_id -ne $applied.result.results.make.rows[0].element_id) { throw 'Workflow was executed again instead of replayed.' }
$bad = @{
    target_document=$target
    steps=@(
        @{ key='first'; tool='horizun_create_elements'; arguments=@{units='feet'; elements=@(@{kind='level'; name=($name+' partial'); elevation=171})} },
        @{ key='bad'; tool='horizun_create_elements'; arguments=@{elements=@(@{kind='wall_opening'; host_id=[long]::MaxValue})} }
    )
}
$preview = Invoke-Probe 'horizun_run_workflow' $bad 'workflow-partial-preview'
$bad.confirmation_token = $preview.result.confirmation_token
$bad.idempotency_key = 'audit-workflow-' + [Guid]::NewGuid().ToString('N')
$bad.dry_run = $false
$failed = Invoke-Probe 'horizun_run_workflow' $bad 'workflow-partial-apply' -ExpectError
if ($failed.result.application.state -ne 'partial' -or $failed.result.completed_steps -ne 1 -or !$failed.result.trace_path) { throw 'Failed workflow did not preserve one completed step and its trace.' }
$again = Invoke-Probe 'horizun_run_workflow' $bad 'workflow-partial-replay' -ExpectError
# The saved response and trace name must be identical: a failed workflow is replayed too.
if (($failed.result | ConvertTo-Json -Depth 100 -Compress) -ne ($again.result | ConvertTo-Json -Depth 100 -Compress)) {
    throw 'Partial replay changed its recorded diagnostic.'
}
Write-Output 'Workflow MCP preview/apply/replay and partial-stop/replay passed.'
