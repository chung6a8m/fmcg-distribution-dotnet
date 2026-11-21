[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Plan,
    [switch]$Apply,
    [Parameter(Mandatory)][string]$ConfigFile,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Apply) {
    [Console]::Error.WriteLine('BLOCKED D04_LIVE_INSTALL_NOT_ENROLLED')
    exit 2
}
if (-not $Plan -or -not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7) {
    [Console]::Error.WriteLine('COLLECTION_FAILED D04_WINDOWS_PLAN_REQUIRED')
    exit 1
}
try {
    . (Join-Path $PSScriptRoot 'DockerWsl.Windows.ps1')
    Assert-D01WindowsEnvironment
    $env:PSModulePath = Get-D01TrustedModulePath
    $python = Get-Command python.exe -CommandType Application -All |
        Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } | Select-Object -First 1
    if (-not $python) { throw 'Python unavailable' }
    & $python.Source -I -S -B (Join-Path $PSScriptRoot '../../tools/app02b_lab/docker_d04.py') --config $ConfigFile --output $OutputDirectory --pwsh (Join-Path $PSHOME 'pwsh.exe')
    if ($LASTEXITCODE -in 0,1,2) { exit $LASTEXITCODE }
} catch {
    [Console]::Error.WriteLine('COLLECTION_FAILED D04_PLAN_ERROR')
}
exit 1
