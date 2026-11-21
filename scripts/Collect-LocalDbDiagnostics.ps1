[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LocalDbErrorKind {
    param([string]$Text)
    switch -Regex ($Text) {
        'Cannot create an automatic instance' { return 'AutomaticInstanceCreationFailed' }
        'automatic instance .* is not created' { return 'AutomaticInstanceNotCreated' }
        'Start of LocalDB instance .* failed' { return 'InstanceStartFailed' }
        'access (?:is )?denied|permission denied' { return 'AccessDenied' }
        'Login timeout expired' { return 'LoginTimeout' }
        'network-related or instance-specific error' { return 'ConnectionFailed' }
        'Sqlcmd: Error:' { return 'SqlClientError' }
        default { return $null }
    }
}

function Invoke-LocalDbProbe {
    param([string]$Executable, [string[]]$Arguments, [int]$TimeoutMilliseconds = 3000)

    $result = [ordered]@{ status = 'OK'; exitCode = $null; stdout = ''; stderr = ''; truncated = $false }
    $process = [Diagnostics.Process]::new()
    $started = $false
    try {
        $command = Get-Command -Name $Executable -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $command) {
            $result.status = 'COMMAND_NOT_FOUND'
            return $result
        }
        $process.StartInfo.FileName = $command.Source
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        foreach ($arg in $Arguments) { $process.StartInfo.ArgumentList.Add($arg) }

        $watch = [Diagnostics.Stopwatch]::StartNew()
        $started = $process.Start()
        $streams = @(
            @{ key = 'stdout'; reader = $process.StandardOutput }
            @{ key = 'stderr'; reader = $process.StandardError }
        )
        foreach ($stream in $streams) {
            $stream.buffer = [char[]]::new(1024)
            $stream.read = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length)
        }
        while ($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
            foreach ($stream in $streams) {
                if ($null -eq $stream.read -or -not $stream.read.IsCompleted) { continue }
                $count = $stream.read.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stream.read = $null
                    continue
                }
                $remaining = 8192 - $result[$stream.key].Length
                $result[$stream.key] += [string]::new($stream.buffer, 0, [Math]::Min($count, $remaining))
                if ($count -gt $remaining) { $result.truncated = $true }
                $stream.read = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length)
            }
            if ($null -eq $streams[0].read -and $null -eq $streams[1].read -and $process.HasExited) {
                $result.exitCode = $process.ExitCode
                if ($result.exitCode -ne 0) { $result.status = 'COMMAND_FAILED' }
                return $result
            }
            Start-Sleep -Milliseconds 15
        }
        $result.status = 'TIMEOUT'
    }
    catch {
        $cause = $_.Exception.GetBaseException()
        $result.status = if ($cause -is [UnauthorizedAccessException] -or
            ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 5)) {
            'ACCESS_DENIED'
        } else { 'PROBE_FAILED' }
    }
    finally {
        try {
            if ($started -and -not $process.HasExited) { $process.Kill($true) }
        } catch { $result.status = 'CLEANUP_FAILED' }
        $process.Dispose()
    }
    return $result
}

function Get-LocalDbToolVersion {
    param([string]$Name)
    try {
        $tool = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $tool) { return @{ status = 'COMMAND_NOT_FOUND' } }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($tool.Source)
        return @{
            status = 'OK'; executable = $Name
            version = '{0}.{1}.{2}.{3}' -f $version.FileMajorPart, $version.FileMinorPart,
                $version.FileBuildPart, $version.FilePrivatePart
        }
    } catch [UnauthorizedAccessException] { return @{ status = 'ACCESS_DENIED' } }
    catch { return @{ status = 'PROBE_FAILED' } }
}

function Get-LocalDbLogMetadata {
    $profile = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrEmpty($profile)) { return @{ status = 'PROFILE_UNAVAILABLE'; files = @() } }
    $relative = 'Microsoft\Microsoft SQL Server Local DB\Instances\MSSQLLocalDB'
    $files = foreach ($name in 'error.log', 'error1.log', 'error2.log') {
        try {
            $file = Get-Item -LiteralPath (Join-Path $profile "$relative\$name") -ErrorAction Stop
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                @{ location = "%LOCALAPPDATA%\$relative\$name"; status = 'UNEXPECTED_FILE_TYPE' }
                continue
            }
            @{
                location = "%LOCALAPPDATA%\$relative\$name"; status = 'OK'; sizeBytes = $file.Length
                createdUtc = $file.CreationTimeUtc.ToString('o'); modifiedUtc = $file.LastWriteTimeUtc.ToString('o')
            }
        } catch [System.Management.Automation.ItemNotFoundException] { }
        catch [UnauthorizedAccessException] { @{ location = "%LOCALAPPDATA%\$relative\$name"; status = 'ACCESS_DENIED' } }
        catch { @{ location = "%LOCALAPPDATA%\$relative\$name"; status = 'PROBE_FAILED' } }
    }
    return @{ status = 'OK'; files = @($files) }
}

function Get-LocalDbEventMetadata {
    $since = [DateTime]::Now.AddMinutes(-10)
    $failures = [Collections.Generic.List[string]]::new()
    # Query the two provider families separately. An unregistered wildcard can
    # terminate a combined Get-WinEvent query before the other provider is read.
    # Event 280 is documented under SQL Server Local Database Runtime 12.0.
    $events = @(foreach ($provider in 'SQLLocalDB*', 'SQL Server Local Database Runtime *') {
        try {
            Get-WinEvent -FilterHashtable @{
                LogName = 'Application'; ProviderName = $provider; StartTime = $since
            } -MaxEvents 8 -ErrorAction Stop
        } catch [UnauthorizedAccessException] {
            $failures.Add('ACCESS_DENIED')
        } catch {
            if ($_.FullyQualifiedErrorId.Split(',')[0] -notin 'NoMatchingEventsFound', 'NoMatchingProvidersFound') {
                $failures.Add('PROBE_FAILED')
            }
        }
    })
    return @{
        status = if ($events.Count -gt 0) {
            if ($failures.Count -gt 0) { 'PARTIAL' } else { 'OK' }
        } elseif ($failures.Count -gt 0) { $failures[0] } else { 'NO_MATCH' }
        # Keep only eight records across both providers. Never access the payload.
        events = @($events | Sort-Object TimeCreated -Descending | Select-Object -First 8 | ForEach-Object {
            @{ id = $_.Id; level = $_.Level; utc = $_.TimeCreated.ToUniversalTime().ToString('o') }
        })
    }
}

function Write-LocalDbDiagnostics {
    $startedUtc = [DateTime]::UtcNow.ToString('o')
    $list = Invoke-LocalDbProbe 'SqlLocalDB.exe' @('info')
    $instance = Invoke-LocalDbProbe 'SqlLocalDB.exe' @('info', 'MSSQLLocalDB')
    $instanceText = ($instance.stdout + [Environment]::NewLine + $instance.stderr) -replace '\r+\n', [Environment]::NewLine
    $versions = Get-LocalDbToolVersion 'SqlLocalDB.exe'
    $sqlcmd = Get-LocalDbToolVersion 'SQLCMD.EXE'

    # Only the known instance identity and typed fields survive projection.
    $errorKind = Get-LocalDbErrorKind $instanceText
    $instanceVersion = [regex]::Match($instanceText, '(?im)^Version:[ \t]*(\d+(?:\.\d+){1,3})[ \t]*\r?$').Groups[1].Value
    $state = [regex]::Match($instanceText, '(?im)^State:[ \t]*(Running|Stopped)[ \t]*\r?$').Groups[1].Value
    $autoCreate = [regex]::Match($instanceText, '(?im)^Auto-create:[ \t]*(Yes|No)[ \t]*\r?$').Groups[1].Value
    $lastStart = [regex]::Match($instanceText, '(?im)^Last start time:[ \t]*([0-9/ :.\-]{6,32}(?:AM|PM)?)[ \t]*\r?$').Groups[1].Value

    $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $processes = try {
        @{ status = 'OK'; sqlServerInRunnerSession = @(Get-Process -Name sqlservr -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq $currentSession).Count }
    } catch { @{ status = 'PROBE_FAILED' } }
    $services = try {
        $service = Get-Service -Name 'MSSQL$MSSQLLocalDB' -ErrorAction SilentlyContinue
        @{ status = if ($null -eq $service) { 'NO_MATCH' } else { $service.Status.ToString() } }
    } catch [UnauthorizedAccessException] { @{ status = 'ACCESS_DENIED' } }
    catch { @{ status = 'PROBE_FAILED' } }

    $logs = Get-LocalDbLogMetadata
    $events = Get-LocalDbEventMetadata
    $partial = @($list.status, $instance.status, $versions.status, $sqlcmd.status, $processes.status,
        $services.status, $logs.status, $events.status) | Where-Object { $_ -notin 'OK', 'NO_MATCH', 'Running', 'Stopped' }
    $partialLog = $logs.files | Where-Object status -ne 'OK'
    $bootstrap = foreach ($line in ($env:LAB_E02_BOOTSTRAP -split '\|', 4)) {
        if ($line -cmatch '^(info|start|sqlcmd),(-?\d{1,10}|unavailable),(None|AutomaticInstanceCreationFailed|AutomaticInstanceNotCreated|InstanceStartFailed|AccessDenied|LoginTimeout|ConnectionFailed|SqlClientError|LaunchFailed),(0x[0-9a-fA-F]{8}|None)$') {
            @{
                stage = $Matches[1]; exitCode = if ($Matches[2] -ne 'unavailable') { [long]$Matches[2] }
                error = $Matches[3]; nativeCode = if ($Matches[4] -ne 'None') { $Matches[4] }
            }
        }
    }
    $report = [ordered]@{
        startedUtc = $startedUtc; capturedUtc = [DateTime]::UtcNow.ToString('o')
        status = if ($partial -or $partialLog -or $list.truncated -or $instance.truncated -or
            $errorKind -or !$instanceVersion -or !$state -or !$autoCreate) { 'PARTIAL' } else { 'OK' }
        sourceHeadSha = if ($env:LAB_E02_SOURCE_HEAD_SHA -cmatch '^[a-f0-9]{40}$') { $env:LAB_E02_SOURCE_HEAD_SHA }
        workflowSha = if ($env:GITHUB_SHA -cmatch '^[a-f0-9]{40}$') { $env:GITHUB_SHA }
        runId = if ($env:GITHUB_RUN_ID -cmatch '^[0-9]{1,20}$') { $env:GITHUB_RUN_ID }
        runAttempt = if ($env:GITHUB_RUN_ATTEMPT -cmatch '^[0-9]{1,10}$') { $env:GITHUB_RUN_ATTEMPT }
        runner = @{
            osVersion = [Environment]::OSVersion.Version.ToString()
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            imageVersion = if ($env:ImageVersion -cmatch '^[0-9.]{1,32}$') { $env:ImageVersion }
        }
        bootstrap = @($bootstrap)
        instanceList = @{
            status = $list.status; exitCode = $list.exitCode; truncated = $list.truncated
            targetListed = $list.stdout -match '(?im)^[ \t]*MSSQLLocalDB[ \t]*\r*$'
        }
        instance = @{
            name = 'MSSQLLocalDB'; status = $instance.status; exitCode = $instance.exitCode
            truncated = $instance.truncated; version = $instanceVersion; state = $state
            autoCreate = $autoCreate; lastStartLocal = $lastStart; error = $errorKind
        }
        tools = @{ localDb = $versions; sqlcmd = $sqlcmd }
        processes = $processes; services = $services; logs = $logs; applicationEvents = $events
    }

    $root = Join-Path $PSScriptRoot '../TestResults/LAB-E02'
    for ($directory = [IO.DirectoryInfo]::new($root); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'LocalDB diagnostics output directory contains a reparse point.'
        }
    }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $path = Join-Path $root "localdb-$([Guid]::NewGuid().ToString('N')).json"
    $temporary = "$path.tmp"
    try {
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8
        [IO.File]::Move($temporary, $path)
    } finally {
        [IO.File]::Delete($temporary)
    }
    Write-Host "LAB-E02 LocalDB collector $($report.status). Report: $([IO.Path]::GetFileName($path))"
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Write-LocalDbDiagnostics }
    catch {
        # Avoid PowerShell's default rendering of potentially private paths.
        Write-Host "LAB-E02 LocalDB collector FAILED ($($_.Exception.GetBaseException().GetType().Name))."
        exit 1
    }
}
