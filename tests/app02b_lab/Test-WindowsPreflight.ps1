$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
foreach ($name in @('Lab.Common.psm1', 'Test-LabPrerequisites.ps1')) {
    $tokens = $null
    $parseErrors = $null
    $file = Join-Path $root "scripts/lab/$name"
    [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw "PowerShell syntax invalid in $name" }
}
$module = Import-Module (Join-Path $root 'scripts/lab/Lab.Common.psm1') -Force -PassThru
& $module {
    $nativePowerShell = Join-Path $PSHOME 'pwsh.exe'
    $version = Get-LabNativeVersion $nativePowerShell @('-Version') '^PowerShell (\d+\.\d+\.\d+)'
    if ($version -ne $PSVersionTable.PSVersion.ToString()) { throw 'Version probe failed' }
    if ($null -ne (Get-LabNativeVersion '' @('-Version') '^PowerShell (\d+\.\d+\.\d+)')) {
        throw 'Missing tool was treated as present'
    }

    $script:fixtureEndpoints = @('npipe:////./pipe/docker_engine')
    function Get-LabDockerEndpoints { ,$script:fixtureEndpoints }

    function Invoke-LabNative {
        param([string]$Executable, [string[]]$Arguments)
        if ($Arguments[0] -ne '--host' -or $Arguments[1] -notmatch '^npipe:////\./pipe/docker') {
            throw 'Docker inventory attempted a non-local connection'
        }
        if ($Arguments[1] -like '*dockerDesktopLinuxEngine') {
            return [pscustomobject]@{ Code = 1; Stdout = '' }
        }
        $id = 'a' * 64
        if ($Arguments[2] -eq 'network' -and $Arguments[3] -eq 'ls') {
            return [pscustomobject]@{ Code = 0; Stdout = "$id" }
        }
        if ($Arguments[2] -eq 'network' -and $Arguments[3] -eq 'inspect' -and $Arguments[4] -eq $id) {
            return [pscustomobject]@{
                Code = 0
                Stdout = '[{"Driver":"bridge","IPAM":{"Config":[{"Subnet":"172.28.200.0/24"},{"Subnet":"fd00::/64"}]}}]'
            }
        }
        throw 'Docker inventory invoked an unexpected command'
    }

    $networks = Get-LabDockerNetworks 'docker.exe'
    if ($networks.Count -ne 1 -or $networks[0] -ne '172.28.200.0/24') {
        throw 'Docker IPv4 inventory mismatch'
    }
    $script:fixtureEndpoints += 'npipe:////./pipe/dockerDesktopLinuxEngine'
    if ($null -ne (Get-LabDockerNetworks 'docker.exe')) { throw 'Partial Docker coverage was accepted' }
    if ($null -ne (Get-LabDockerNetworks '')) { throw 'Docker daemon without CLI was treated as absent' }
    $script:fixtureEndpoints = @()
    $absent = Get-LabDockerNetworks ''
    if ($null -eq $absent -or $absent.Count -ne 0) { throw 'Absent Docker reported unknown ranges' }
}
Write-Output 'PowerShell parser, import, native helper and synthetic local Docker probes passed'
