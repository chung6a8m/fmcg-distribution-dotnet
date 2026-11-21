#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')]
    [Parameter(Mandatory, ParameterSetName = 'Resume')]
    [string] $Config,

    [Parameter(Mandatory)]
    [string] $RunId,

    [Parameter(Mandatory)]
    [string] $EvidenceRoot,

    [string] $Selection = 'all',

    [Parameter(ParameterSetName = 'Resume')]
    [Parameter(ParameterSetName = 'Validate')]
    [string] $Checkpoint,

    [Parameter(Mandatory, ParameterSetName = 'Resume')]
    [switch] $Resume,

    [Parameter(Mandatory, ParameterSetName = 'Validate')]
    [switch] $Validate
)

$python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $python) {
    Write-Output '{"validation":"BLOCKED","originalGateStatus":"NOT_VERIFIED","issues":[{"code":"PYTHON_UNAVAILABLE"}]}'
    exit 2
}

$entryPoint = Join-Path $PSScriptRoot '../../tools/app02b_lab/run.py'
$labArguments = @('-I', '-S', '-B', $entryPoint, "--run-id=$RunId", "--evidence-root=$EvidenceRoot", "--selection=$Selection")
if ($Validate) {
    $labArguments += '--validate'
} else {
    $labArguments += "--config=$Config"
}
if ($Checkpoint) {
    $labArguments += "--checkpoint=$Checkpoint"
}
if ($Resume) {
    $labArguments += '--resume'
}

$PSNativeCommandUseErrorActionPreference = $false
& $python.Source @labArguments
exit $LASTEXITCODE
