[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$ConfigFile,
    [string]$PlanFile,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $ConfigFile -or -not $PlanFile -or -not $OutputDirectory) {
    [Console]::Error.WriteLine('COLLECTION_FAILED INVALID_D02_ARGUMENTS')
    exit 1
}
. ([IO.Path]::Combine($PSScriptRoot, 'DockerRehearsal.Common.ps1'))
exit (Invoke-D02Controller -Arguments @('verify', '--config-file', $ConfigFile, '--plan-file', $PlanFile,
        '--output-directory', $OutputDirectory))
