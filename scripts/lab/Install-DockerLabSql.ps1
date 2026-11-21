[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Plan,
    [switch]$Apply,
    [Parameter(Mandatory)][string]$ConfigFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PlanFile,
    [string]$EnrollmentFile,
    [string]$EnrollmentSha256
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Plan.IsPresent -eq $Apply.IsPresent -or
    ($Apply -and (-not $PlanFile -or -not $EnrollmentFile -or -not $EnrollmentSha256)) -or
    ($Plan -and ($PlanFile -or $EnrollmentFile -or $EnrollmentSha256))) {
    [Console]::Error.WriteLine('COLLECTION_FAILED INVALID_D04_ARGUMENTS')
    exit 1
}
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7) {
    [Console]::Error.WriteLine('COLLECTION_FAILED D04_WINDOWS_REQUIRED')
    exit 1
}
try {
    . (Join-Path $PSScriptRoot 'DockerWsl.Windows.ps1')
    Assert-D01WindowsEnvironment
    $env:PSModulePath = Get-D01TrustedModulePath
    $python = Get-Command python.exe -CommandType Application -All |
        Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } | Select-Object -First 1
    if (-not $python) { throw 'Python unavailable' }
    $parameters = @('--config', $ConfigFile, '--output', $OutputDirectory, '--pwsh', (Join-Path $PSHOME 'pwsh.exe'))
    if ($Apply) {
        $parameters += @('--apply', '--plan', $PlanFile, '--enrollment', $EnrollmentFile, '--enrollment-sha256', $EnrollmentSha256)
    }
    & $python.Source -I -S -B (Join-Path $PSScriptRoot '../../tools/app02b_lab/docker_d04.py') @parameters
    if ($LASTEXITCODE -in 0,1,2) { exit $LASTEXITCODE }
} catch {
    [Console]::Error.WriteLine('COLLECTION_FAILED D04_PLAN_ERROR')
}
exit 1
