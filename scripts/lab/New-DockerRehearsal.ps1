[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Plan,
    [switch]$Apply,
    [string]$ConfigFile,
    [string]$OutputDirectory,
    [string]$PlanFile,
    [string]$EnrollmentFile,
    [string]$EnrollmentSha256
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Plan.IsPresent -eq $Apply.IsPresent -or -not $ConfigFile -or -not $OutputDirectory -or
        ($Plan -and ($PlanFile -or $EnrollmentFile -or $EnrollmentSha256)) -or
        ($Apply -and (-not $PlanFile -or -not $EnrollmentFile -or -not $EnrollmentSha256))) {
    [Console]::Error.WriteLine('COLLECTION_FAILED INVALID_D02_ARGUMENTS')
    exit 1
}
. ([IO.Path]::Combine($PSScriptRoot, 'DockerRehearsal.Common.ps1'))
$parameters = @('plan', '--config-file', $ConfigFile, '--output-directory', $OutputDirectory)
if ($Apply) {
    $parameters[0] = 'apply'
    $parameters += @('--plan-file', $PlanFile, '--enrollment-file', $EnrollmentFile,
        '--enrollment-sha256', $EnrollmentSha256)
}
exit (Invoke-D02Controller -Arguments $parameters)
