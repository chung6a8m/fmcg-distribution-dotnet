# Internal LAB-D01 collector. Stdout is consumed in memory by docker_preflight.py.
param([switch]$DataDisk, [switch]$VerifySignatures)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)

try {
    . (Join-Path $PSScriptRoot 'DockerWsl.Windows.ps1')
    Assert-D01WindowsEnvironment
    $systemDirectory = [Environment]::SystemDirectory
    $env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' +
        (Join-Path $systemDirectory 'WindowsPowerShell\v1.0\Modules')
    if ($VerifySignatures) {
        $request = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd())
        foreach ($path in $request.paths) {
            if (-not (Test-D01Authenticode -Path $path -Publisher $request.publisher)) {
                exit 4
            }
        }
        [Console]::WriteLine('VERIFIED')
        exit 0
    }
    if ($DataDisk) {
        $path = [Console]::ReadLine()
        $image = Get-DiskImage -ImagePath $path
        @{ attached = ($image.Attached -eq $true); capacity = [int64]$image.Size } |
            ConvertTo-Json -Compress
        exit 0
    }
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -OperationTimeoutSec 10
    $machine = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography' -Name MachineGuid).MachineGuid
    $desktop = @(Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Path } | Where-Object { $_ } | Select-Object -Unique)
    $desktopVersion = if ($desktop.Count -eq 1) {
        $file = Get-Item -LiteralPath $desktop[0]
        if ($file.VersionInfo.ProductVersion -match '^\d+\.\d+(?:\.\d+){0,2}') { $Matches[0] }
    }
    $ports = @(
        Get-NetTCPConnection -ErrorAction SilentlyContinue -ErrorVariable tcpErrors |
            ForEach-Object { [int]$_.LocalPort }
        Get-NetUDPEndpoint -ErrorAction SilentlyContinue -ErrorVariable udpErrors |
            ForEach-Object { [int]$_.LocalPort }
    )
    if ($tcpErrors -or $udpErrors) { exit 3 }
    $netsh = Join-Path $systemDirectory 'netsh.exe'
    if (-not (Test-D01Authenticode -Path $netsh -Publisher 'Microsoft Corporation')) { exit 4 }
    $excluded = [Collections.Generic.List[object]]::new()
    foreach ($family in 'ipv4', 'ipv6') {
        foreach ($transport in 'tcp', 'udp') {
            $lines = & $netsh interface $family show excludedportrange "protocol=$transport" 2>$null
            if ($LASTEXITCODE -ne 0 -or -not ($lines -match '^\s*-{2,}\s+-{2,}\s*$')) { exit 3 }
            foreach ($line in $lines) {
                if ($line -match '^\s*(\d+)\s+(\d+)\s*\*?\s*$') {
                    $excluded.Add(@([int]$Matches[1], [int]$Matches[2]))
                }
            }
        }
    }
    $routes = @(Get-NetRoute -AddressFamily IPv4 | Where-Object { $_.DestinationPrefix -ne '0.0.0.0/0' } |
        Select-Object -ExpandProperty DestinationPrefix -Unique)
    $dns = @(
        foreach ($registry in 'mcr.microsoft.com', 'registry-1.docker.io') {
            try {
                Resolve-DnsName -Name $registry -Type A -DnsOnly -QuickTimeout |
                    Where-Object { $_.Type -eq 'A' } | Select-Object -First 1
            } catch { }
        }
    )
    @{
        machine = $machine
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        build = [int]$os.BuildNumber
        freeRam = [int64]$os.FreePhysicalMemory * 1KB
        totalRam = [int64]$os.TotalVisibleMemorySize * 1KB
        ports = @($ports | Sort-Object -Unique)
        excluded = $excluded.ToArray()
        routes = $routes
        dns = $dns.Count
        pwsh = $PSVersionTable.PSVersion.ToString()
        desktop = $desktopVersion
        desktopPath = if ($desktop.Count -eq 1) { $desktop[0] }
    } | ConvertTo-Json -Compress -Depth 6
    exit 0
} catch [System.UnauthorizedAccessException] {
    exit 3
} catch [Microsoft.Management.Infrastructure.CimException] {
    if ($_.Exception.StatusCode.ToString() -eq 'AccessDenied') { exit 3 }
    exit 1
} catch {
    exit 1
}
