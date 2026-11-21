[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$EnvironmentFile,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
try {
    if ($PSVersionTable.PSVersion.Major -ne 7 -or -not $IsWindows) {
        [Console]::Error.WriteLine('COLLECTION_FAILED WINDOWS_POWERSHELL_7_REQUIRED')
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($EnvironmentFile) -or [string]::IsNullOrWhiteSpace($OutputDirectory)) {
        [Console]::Error.WriteLine('COLLECTION_FAILED INVALID_ARGUMENTS')
        exit 1
    }
    $python = @(Get-Command python.exe -CommandType Application -All -ErrorAction Ignore |
        Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } |
        Select-Object -First 1)
    if ($python.Count -ne 1) {
        [Console]::Error.WriteLine('COLLECTION_FAILED MISSING_TOOL_PYTHON')
        exit 1
    }
    $entry = Join-Path $PSScriptRoot '../../tools/app02b_lab/preflight.py'
    & $python[0].Source -I -S -B $entry $EnvironmentFile $OutputDirectory (Join-Path $PSHOME 'pwsh.exe')
    $status = $LASTEXITCODE
    if ($status -in 0, 1, 2) { exit $status }
    [Console]::Error.WriteLine('COLLECTION_FAILED PREFLIGHT_RUNTIME_FAILED')
    exit 1
} catch {
    [Console]::Error.WriteLine('COLLECTION_FAILED PREFLIGHT_RUNTIME_FAILED')
    exit 1
}
