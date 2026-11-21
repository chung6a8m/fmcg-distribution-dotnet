function Invoke-D02Controller {
    param([string[]]$Arguments)

    $previousModulePath = $env:PSModulePath
    try {
        if ($PSVersionTable.PSVersion.Major -ne 7 -or -not $IsWindows) {
            [Console]::Error.WriteLine('COLLECTION_FAILED WINDOWS_POWERSHELL_7_REQUIRED')
            return 1
        }
        . ([IO.Path]::Combine($PSScriptRoot, 'DockerWsl.Windows.ps1'))
        Assert-D01WindowsEnvironment
        $env:PSModulePath = Get-D01TrustedModulePath
        $python = Get-Command python.exe -CommandType Application -All -ErrorAction Ignore |
            Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } | Select-Object -First 1
        if (-not $python) {
            [Console]::Error.WriteLine('COLLECTION_FAILED MISSING_TOOL_PYTHON')
            return 1
        }
        $entry = Join-Path $PSScriptRoot '../../tools/app02b_lab/docker_d02.py'
        & $python.Source -I -S -B $entry @Arguments --pwsh (Join-Path $PSHOME 'pwsh.exe') |
            ForEach-Object { [Console]::Out.WriteLine($_) }
        if ($LASTEXITCODE -in 0, 1, 2) { return $LASTEXITCODE }
        [Console]::Error.WriteLine('COLLECTION_FAILED D02_RUNTIME_ERROR')
        return 1
    } catch {
        if ($_.Exception.Message -in 'WINDOWS_ROOT_ENV_OVERRIDE', 'WINDOWS_ROOT_UNAVAILABLE', 'WINDOWS_64_BIT_REQUIRED') {
            [Console]::Error.WriteLine("COLLECTION_FAILED $($_.Exception.Message)")
        } else {
            [Console]::Error.WriteLine('COLLECTION_FAILED D02_RUNTIME_ERROR')
        }
        return 1
    } finally {
        $env:PSModulePath = $previousModulePath
    }
}
