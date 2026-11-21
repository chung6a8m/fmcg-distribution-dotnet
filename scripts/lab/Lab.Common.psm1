# Local read-only probes. Raw observations travel to the evaluator over stdout.
Set-StrictMode -Version Latest

function Invoke-LabProbe {
    param([scriptblock]$Read)
    try { & $Read } catch { return $null }
}

function Find-LabApplication {
    param([string]$Name)
    $command = Get-Command $Name -CommandType Application -All -ErrorAction Ignore |
        Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } |
        Select-Object -First 1
    if ($command) { $command.Source }
}

function Invoke-LabNative {
    param([string]$Executable, [string[]]$Arguments, [int]$TimeoutMilliseconds = 5000)
    if (-not $Executable) { return $null }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    [void]$start.Environment.Remove('DOCKER_CONTEXT')
    [void]$start.Environment.Remove('DOCKER_HOST')
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo = $start
        if (-not $process.Start()) { return $null }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            return $null
        }
        if (-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { return $null }
        [pscustomobject]@{ Code = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    } catch {
        return $null
    } finally {
        $process.Dispose()
    }
}

function Get-LabNativeVersion {
    param([string]$Executable, [string[]]$Arguments, [string]$Pattern, [switch]$OnStderr,
          [switch]$AllowHelpExitCode)
    if (-not $Executable) { return $null }
    $run = Invoke-LabNative -Executable $Executable -Arguments $Arguments
    if ($null -eq $run -or ($run.Code -ne 0 -and -not ($AllowHelpExitCode -and $run.Code -eq 1))) {
        return 'UNAVAILABLE'
    }
    $response = if ($OnStderr) { $run.Stderr } else { $run.Stdout }
    $found = [regex]::Match($response, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($found.Success) { return $found.Groups[1].Value }
    return 'UNAVAILABLE'
}

function Get-LabHostTools {
    param([string]$HyperVModuleVersion)
    $node = Find-LabApplication 'node.exe'
    $docker = Find-LabApplication 'docker.exe'
    $npmWrapper = Find-LabApplication 'npm.cmd'
    $npm = foreach ($executable in @($node, $npmWrapper)) {
        if (-not $executable) { continue }
        $candidate = Join-Path (Split-Path -Parent $executable) 'node_modules/npm/bin/npm-cli.js'
        if ([IO.File]::Exists($candidate)) { $candidate; break }
    }
    $npmVersion = if ($node -and $npm) {
        # Read bundled package metadata without npm initialization or cache writes.
        $value = Invoke-LabProbe {
            $manifest = Join-Path (Split-Path -Parent (Split-Path -Parent $npm)) 'package.json'
            (Get-Content -Raw -LiteralPath $manifest -ErrorAction Stop | ConvertFrom-Json -Depth 20).version
        }
        if ($value -match '^\d+\.\d+\.\d+$') { $value } else { 'UNAVAILABLE' }
    } elseif ($npmWrapper) { 'UNAVAILABLE' }
    $vNum = '(\d+\.\d+\.\d+)(?=\s|$)'
    $wsl = Get-Item (Join-Path $env:SystemRoot 'System32/wsl.exe') -ErrorAction Ignore
    $wslVersion = if ($wsl -and ($wsl.VersionInfo.FileVersion -match '^\s*(\d+\.\d+\.\d+(?:\.\d+)?)')) {
        $Matches[1]
    } elseif ($wsl) { 'UNAVAILABLE' }
    $compose = Get-LabNativeVersion $docker @('compose', 'version', '--short') ('^\s*v?' + $vNum)
    if (-not $compose -or $compose -eq 'UNAVAILABLE') {
        $standalone = Find-LabApplication 'docker-compose.exe'
        if ($standalone) { $compose = Get-LabNativeVersion $standalone @('version', '--short') ('^\s*v?' + $vNum) }
    }
    @{
        git = Get-LabNativeVersion (Find-LabApplication 'git.exe') @('--version') '^\s*git version (\d+\.\d+\.\d+)(?:\.windows\.\d+)?\s*$'
        gh = Get-LabNativeVersion (Find-LabApplication 'gh.exe') @('--version') ('^\s*gh version ' + $vNum)
        pwsh = $PSVersionTable.PSVersion.ToString()
        dotnet = Get-LabNativeVersion (Find-LabApplication 'dotnet.exe') @('--version') ('(?m)^\s*' + $vNum + '\s*$')
        node = Get-LabNativeVersion $node @('--version') ('^\s*v' + $vNum + '\s*$')
        npm = $npmVersion
        python = Get-LabNativeVersion (Find-LabApplication 'python.exe') @('-I', '-S', '-B', '--version') ('^\s*Python ' + $vNum + '\s*$')
        openssl = Get-LabNativeVersion (Find-LabApplication 'openssl.exe') @('version') '^\s*OpenSSL (\d+\.\d+\.\d+)[a-z]?(?=\s|$)'
        ssh = Get-LabNativeVersion (Find-LabApplication 'ssh.exe') @('-V') '^\s*OpenSSH(?:_for_Windows)?_(\d+\.\d+(?:\.\d+|p\d+))(?=[,\s]|$)' -OnStderr
        sqlcmd = Get-LabNativeVersion (Find-LabApplication 'sqlcmd.exe') @('-?') '(?mi)^.*Version\s+v?(\d+\.\d+\.\d+(?:\.\d+)?)(?=\s|$)' -AllowHelpExitCode
        docker = Get-LabNativeVersion $docker @('--version') '^\s*Docker version (\d+\.\d+\.\d+)(?=[,\s]|$)'
        compose = $compose
        hyperv = if ($HyperVModuleVersion) { $HyperVModuleVersion }
        localdb = Get-LabNativeVersion (Find-LabApplication 'SqlLocalDB.exe') @('versions') '(?m)(\d+\.\d+\.\d+(?:\.\d+)?)(?=\s|$)'
        wsl = $wslVersion
    }
}

function Get-LabDockerEndpoints {
    $pipes = [IO.Directory]::GetFiles('\\.\pipe\')
    $endpoints = foreach ($name in @('docker_engine', 'dockerDesktopLinuxEngine')) {
        if ("\\.\pipe\$name" -in $pipes) { "npipe:////./pipe/$name" }
    }
    return ,@($endpoints)
}

function Get-LabDockerNetworks {
    param([string]$Docker)
    $ranges = [Collections.Generic.List[string]]::new()
    $endpoints = Get-LabDockerEndpoints
    if (-not $endpoints.Count) { return ,@() }
    if (-not $Docker) { return $null }
    # Only query local pipes confirmed present. An unreadable daemon blocks coverage.
    foreach ($endpoint in $endpoints) {
        $list = Invoke-LabNative $Docker @('--host', $endpoint, 'network', 'ls', '--quiet', '--no-trunc')
        if ($null -eq $list -or $list.Code -ne 0) { return $null }
        $ids = @($list.Stdout -split '\r?\n' | Where-Object { $_ })
        if ($ids.Count -gt 128) { return $null }
        if ($ids.Count) {
            if (@($ids | Where-Object { $_ -cnotmatch '^[0-9a-f]{64}$' }).Count) { return $null }
            $detail = Invoke-LabNative $Docker (@('--host', $endpoint, 'network', 'inspect') + $ids)
            if ($null -eq $detail -or $detail.Code -ne 0) { return $null }
            $documents = $detail.Stdout | ConvertFrom-Json -Depth 20
            if (@($documents).Count -ne $ids.Count) { return $null }
            foreach ($network in $documents) {
                if ($network.Driver -in 'host', 'null') { continue }
                foreach ($config in @($network.IPAM.Config)) {
                    if ($null -ne $config -and $config.Subnet -and $config.Subnet -notlike '*:*') {
                        $ranges.Add([string]$config.Subnet)
                    }
                }
            }
        }
    }
    return ,@($ranges | Sort-Object -Unique)
}

function Get-LabHnsNetworks {
    # WSL, container and Hyper-V networks exposed by Windows HNS.
    try {
        $service = Get-Service -Name hns -ErrorAction Stop
    } catch {
        if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return ,@() }
        throw
    }
    if ($service.Status -eq 'Stopped') { return ,@() }
    if ($service.Status -ne 'Running' -or -not (Get-Command Get-HnsNetwork -ListImported -CommandType Function, Cmdlet -ErrorAction Ignore)) {
        return $null
    }
    $ranges = foreach ($network in Get-HnsNetwork -ErrorAction Stop) {
        $subnets = $network.PSObject.Properties['Subnets']
        if (-not $subnets) { throw 'NETWORK_INVENTORY_UNAVAILABLE' }
        foreach ($subnet in $subnets.Value) {
            if ($subnet.AddressPrefix -and $subnet.AddressPrefix -notlike '*:*') { $subnet.AddressPrefix }
        }
    }
    return ,@($ranges | Sort-Object -Unique)
}

function Get-LabClock {
    $system32 = Join-Path $env:SystemRoot 'System32'
    $service = Get-Service -Name W32Time -ErrorAction Ignore
    $serviceState = if ($service) { [string]$service.Status } else { 'Absent' }
    $source = Invoke-LabNative (Join-Path $system32 'w32tm.exe') @('/query', '/source')
    $kind = if ($source -and $source.Code -eq 0) {
        $name = $source.Stdout.Trim()
        if ($name -match '^(?:Local CMOS Clock|Free-running System Clock|localhost|127\..*|::1)$') {
            'LOCAL_CLOCK'
        } elseif ($name -match 'VM IC Time Synchronization Provider') {
            'VM_CLOCK'
        } elseif ($name -match '^[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\.?(?:,0x[a-fA-F0-9]+)?$') {
            'EXTERNAL_CONFIGURED'
        }
    }

    $status = Invoke-LabNative (Join-Path $system32 'w32tm.exe') @('/query', '/status')
    $lastSync = if ($status -and $status.Code -eq 0 -and
                    $status.Stdout -match '(?m)^Last Successful Sync Time:\s*([^\r\n]+)') {
        $parsed = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse($Matches[1], [Globalization.CultureInfo]::GetCultureInfo('en-US'),
            [Globalization.DateTimeStyles]::AssumeLocal, [ref]$parsed)) {
            $parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }
    }
    @{ Service = $serviceState; Kind = $kind; LastSyncUtc = $lastSync }
}

function Get-LabWindowsInventory {
    param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z]$')][string]$DriveLetter)
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -OperationTimeoutSec 10 -ErrorAction Stop
    $system = Get-CimInstance -ClassName Win32_ComputerSystem -OperationTimeoutSec 10 -ErrorAction Stop
    $cpus = @(Get-CimInstance -ClassName Win32_Processor -OperationTimeoutSec 10 -ErrorAction Stop)
    if (-not $os -or -not $system -or $cpus.Count -eq 0) { throw 'INVENTORY_COLLECTION_FAILED' }
    $cpu = $cpus[0]
    $edition = Invoke-LabProbe {
        (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID -ErrorAction Stop).EditionID
    }
    $product = Invoke-LabProbe {
        (Get-CimInstance Win32_ComputerSystemProduct -OperationTimeoutSec 10 -ErrorAction Stop).UUID
    }
    if ($product -match '^0{8}-0{4}-0{4}-0{4}-0{12}$|^F{8}-F{4}-F{4}-F{4}-F{12}$') { $product = $null }
    $cpuArch = switch ($cpu.Architecture) { 0 { 'X86' } 5 { 'ARM' } 9 { 'X64' } 12 { 'ARM64' } }

    $feature = Invoke-LabProbe {
        $query = "Name='Microsoft-Hyper-V'"
        $item = Get-CimInstance -ClassName Win32_OptionalFeature -Filter $query -OperationTimeoutSec 10 -ErrorAction Stop
        if (-not $item) { 'Unavailable' } else {
            switch ($item.InstallState) { 1 { 'Enabled' } 2 { 'Disabled' } 3 { 'Unavailable' } }
        }
    }
    $reboot = Invoke-LabProbe {
        $base = @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
        )
        $pending = foreach ($key in $base) { Test-Path -LiteralPath $key -ErrorAction Stop }
        $session = Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction Stop
        ($true -in $pending) -or [bool]$session.GetValue('PendingFileRenameOperations')
    }
    $hv = Invoke-LabProbe {
        $module = Get-Module -ListAvailable -Name Hyper-V | Sort-Object Version -Descending | Select-Object -First 1
        if ($module) { $module.Version.ToString() }
    }
    $switches = if ($feature -eq 'Unavailable' -or $feature -eq 'Disabled') { 0 } else {
        Invoke-LabProbe { @(Get-VMSwitch -ErrorAction Stop).Count }
    }
    $gen2 = if ($feature -eq 'Enabled' -and $system.HypervisorPresent -eq $true) {
        Invoke-LabProbe {
            # Host capabilities only. A Gen2 Linux boot remains untested.
            $newVm = Get-Command New-VM -CommandType Cmdlet, Function -ErrorAction Stop
            $supported = @(Get-VMHostSupportedVersion -ErrorAction Stop |
                Where-Object { [version]$_.Version -ge [version]'5.0' })
            $newVm.Parameters.ContainsKey('Generation') -and $supported.Count -gt 0
        }
    } elseif ($feature -in 'Disabled', 'Unavailable') { $false }

    $fixedDisk = Invoke-LabProbe {
        $query = "DeviceID='{0}:' AND DriveType=3" -f $DriveLetter
        Get-CimInstance -ClassName Win32_LogicalDisk -Filter $query -OperationTimeoutSec 10 -ErrorAction Stop
    }
    $media = Invoke-LabProbe {
        $disk = Get-Partition -DriveLetter $DriveLetter -ErrorAction Stop | Get-Disk -ErrorAction Stop
        if ($disk.UniqueId) {
            $physical = @(Get-PhysicalDisk -ErrorAction Stop | Where-Object UniqueId -EQ $disk.UniqueId)
            if ($physical.Count -eq 1) { [string]$physical[0].MediaType }
        }
    }

    $interfaces = Invoke-LabProbe {
        ,@(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })
    }
    $routes = Invoke-LabProbe {
        ,@(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Select-Object -ExpandProperty DestinationPrefix -Unique)
    }
    $nat = Invoke-LabProbe {
        ,@(Get-NetNat -ErrorAction Stop | ForEach-Object { $_.InternalIPInterfaceAddressPrefix } |
            Where-Object { $_ -and $_ -notlike '*:*' })
    }
    $hns = Invoke-LabProbe {
        $hnsModule = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/Modules/HostNetworkingService/HostNetworkingService.psd1'
        if ([IO.File]::Exists($hnsModule)) {
            Import-Module $hnsModule -SkipEditionCheck -ErrorAction Stop
        }
        Get-LabHnsNetworks
    }
    $docker = Invoke-LabProbe { Get-LabDockerNetworks (Find-LabApplication 'docker.exe') }
    $resolvers = Invoke-LabProbe {
        @(Get-DnsClientServerAddress -ErrorAction Stop | ForEach-Object ServerAddresses |
            Where-Object { $_ } | Sort-Object -Unique).Count
    }
    $memory = Invoke-LabProbe {
        (Get-CimInstance Win32_PhysicalMemory -OperationTimeoutSec 10 -ErrorAction Stop |
            Measure-Object -Property Capacity -Sum).Sum -as [long]
    }
    $clock = Get-LabClock
    $models = @($cpus.Name | Sort-Object -Unique)
    $hostKey = if ($product) { $product.ToLowerInvariant() } elseif ($system.Name) { $system.Name.ToLowerInvariant() }
    $data = @{
        hostKey = $hostKey
        edition = $edition
        version = $os.Version
        build = $os.BuildNumber
        osArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        cpuArch = $cpuArch
        cpuModel = $models -join '; '
        cores = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum -as [long]
        logical = $system.NumberOfLogicalProcessors
        firmwareVirtualization = $null
        slat = $null
        vmMonitor = $null
        hypervisor = $system.HypervisorPresent
        hyperVFeature = $feature
        gen2 = $gen2
        reboot = $reboot
        totalRam = if ($memory -gt 0) { $memory } else { $system.TotalPhysicalMemory }
        freeRam = [long]$os.FreePhysicalMemory * 1024
        diskCapacity = if ($fixedDisk) { $fixedDisk.Size }
        diskFree = if ($fixedDisk) { $fixedDisk.FreeSpace }
        diskMedia = $media
        switchCount = $switches
        dnsCount = $resolvers
        networks = @{ INTERFACE = $interfaces; ROUTE = $routes; WINNAT = $nat; HNS = $hns; DOCKER = $docker }
        timeService = $clock.Service
        timeSourceKind = $clock.Kind
        lastSyncUtc = $clock.LastSyncUtc
        tools = Get-LabHostTools $hv
    }
    foreach ($entry in @{
        firmwareVirtualization = 'VirtualizationFirmwareEnabled'
        slat = 'SecondLevelAddressTranslationExtensions'
        vmMonitor = 'VMMonitorModeExtensions'
    }.GetEnumerator()) {
        $values = @($cpus | ForEach-Object { $_.($entry.Value) })
        if ($null -in $values -or ($system.HypervisorPresent -eq $true -and $false -in $values)) {
            # Hypervisor activity can mask the hardware capability bits in CIM.
            continue
        }
        $data[$entry.Key] = $false -notin $values
    }
    ConvertTo-Json -InputObject $data -Depth 12 -Compress
}

Export-ModuleMember -Function Get-LabWindowsInventory
