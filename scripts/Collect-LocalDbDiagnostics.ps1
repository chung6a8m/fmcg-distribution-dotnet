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
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [int]$TimeoutMilliseconds = 3000,
        [bool]$KillEntireProcessTree = $true,
        [Diagnostics.Stopwatch]$DeadlineWatch = $null,
        [long]$DeadlineMilliseconds = 0
    )

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $remainingBudget = {
        $remaining = [long]$TimeoutMilliseconds - $watch.ElapsedMilliseconds
        if ($null -ne $DeadlineWatch) {
            $remaining = [Math]::Min($remaining, $DeadlineMilliseconds - $DeadlineWatch.ElapsedMilliseconds)
        }
        return $remaining
    }
    $result = [ordered]@{ status = 'OK'; exitCode = $null; stdout = ''; stderr = ''; truncated = $false }
    $process = [Diagnostics.Process]::new()
    $started = $false
    try {
        $command = Get-Command -Name $Executable -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ((& $remainingBudget) -le 0) {
            $result.status = 'TIMEOUT'
            return $result
        }
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

        if ((& $remainingBudget) -le 0) {
            $result.status = 'TIMEOUT'
            return $result
        }
        $started = $process.Start()
        $streams = @(
            @{ key = 'stdout'; reader = $process.StandardOutput }
            @{ key = 'stderr'; reader = $process.StandardError }
        )
        foreach ($stream in $streams) {
            $stream.buffer = [char[]]::new(1024)
            $stream.read = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length)
        }
        while ((& $remainingBudget) -gt 0) {
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
            $sleep = [Math]::Min(15, (& $remainingBudget))
            if ($sleep -gt 0) { Start-Sleep -Milliseconds $sleep }
        }
        $result.status = 'TIMEOUT'
        # A child can inherit the output pipes after its launcher has exited.
        # Preserve that native exit code while reporting the capture timeout.
        if ($process.HasExited) { $result.exitCode = $process.ExitCode }
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
            # An initialization launcher can have spawned the SQL engine. Its
            # timeout must not kill that engine; read-only probes retain the default.
            if ($started -and -not $process.HasExited) { $process.Kill($KillEntireProcessTree) }
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

function Get-LocalDbLogErrorCodes {
    param([string]$Path)

    $result = [ordered]@{ status = 'OK'; bytesRead = 0; truncated = $false; sqlErrors = @() }
    $stream = $null
    try {
        $file = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
        # Check the entire existing path before opening. A benign-looking log
        # filename must not follow a reparse point in its containing directories.
        for ($entry = $file; $null -ne $entry; $entry = $(if ($entry -is [IO.FileInfo]) { $entry.Directory } else { $entry.Parent })) {
            $attributes = [IO.File]::GetAttributes($entry.FullName)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                $result.status = 'REPARSE_POINT'
                return $result
            }
        }
        if ([IO.File]::GetAttributes($file.FullName) -band [IO.FileAttributes]::Directory) {
            $result.status = 'UNEXPECTED_FILE_TYPE'
            return $result
        }

        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $length = $stream.Length
        $limit = 65536
        $header = [byte[]]::new([Math]::Min(3, $length))
        while ($result.bytesRead -lt $header.Length) {
            $read = $stream.Read($header, $result.bytesRead, $header.Length - $result.bytesRead)
            if ($read -eq 0) { throw [IO.EndOfStreamException]::new() }
            $result.bytesRead += $read
        }
        $utf16 = $header.Length -ge 2 -and $header[0] -eq 0xff -and $header[1] -eq 0xfe
        if (($utf16 -and ($length % 2) -ne 0) -or
            ($header.Length -ge 2 -and $header[0] -eq 0xfe -and $header[1] -eq 0xff)) {
            $result.status = 'MALFORMED_CONTENT'
            return $result
        }
        $encoding = if ($utf16) { [Text.UnicodeEncoding]::new($false, $false, $true) }
            else { [Text.UTF8Encoding]::new($false, $true) }
        if ($length -le $limit) {
            $buffer = [byte[]]::new($length)
            [Array]::Copy($header, $buffer, $header.Length)
            $filled = $header.Length
            $decodeFrom = if ($utf16) { 2 }
                elseif ($header.Length -eq 3 -and $header[0] -eq 0xef -and $header[1] -eq 0xbb -and $header[2] -eq 0xbf) { 3 }
                else { 0 }
        } else {
            $result.truncated = $true
            # BOM detection counts toward the same 64 KiB I/O budget.
            $offset = $length - ($limit - $header.Length)
            if ($utf16 -and ($offset % 2) -ne 0) { $offset++ }
            [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
            $buffer = [byte[]]::new($length - $offset)
            $filled = 0
            $decodeFrom = $buffer.Length
        }
        while ($filled -lt $buffer.Length) {
            $read = $stream.Read($buffer, $filled, $buffer.Length - $filled)
            if ($read -eq 0) { throw [IO.EndOfStreamException]::new() }
            $filled += $read
            $result.bytesRead += $read
        }
        if ($result.truncated) {
            # The first tail line may start inside a UTF-8 sequence or UTF-16
            # surrogate pair. Drop it before strict decoding, on code-unit boundaries.
            for ($i = 0; $i -lt $buffer.Length; $i += $(if ($utf16) { 2 } else { 1 })) {
                if ($buffer[$i] -eq 10 -and (-not $utf16 -or $buffer[$i + 1] -eq 0)) {
                    $decodeFrom = $i + $(if ($utf16) { 2 } else { 1 })
                    break
                }
            }
        }
        $text = $encoding.GetString($buffer, $decodeFrom, $buffer.Length - $decodeFrom)
        $records = [Collections.Generic.List[object]]::new()
        $pattern = '(?:^|[ \t])Error:[ \t]*([0-9]{1,10}),[ \t]*Severity:[ \t]*([0-9]{1,3}),[ \t]*State:[ \t]*([0-9]{1,3})\.[ \t]*\r?$'
        foreach ($match in [regex]::Matches($text, $pattern, [Text.RegularExpressions.RegexOptions]::Multiline)) {
            $number = 0; $severity = 0; $state = 0
            if (-not [int]::TryParse($match.Groups[1].Value, [ref]$number) -or
                -not [int]::TryParse($match.Groups[2].Value, [ref]$severity) -or $severity -gt 255 -or
                -not [int]::TryParse($match.Groups[3].Value, [ref]$state) -or $state -gt 255) { continue }
            $records.Add(@{ error = $number; severity = $severity; state = $state })
            if ($records.Count -gt 16) {
                $records.RemoveAt(0)
                $result.truncated = $true
            }
        }
        $result.sqlErrors = @($records.ToArray())
    } catch {
        $cause = $_.Exception.GetBaseException()
        # UnauthorizedAccessException can wrap a platform IOException. Preserve
        # the typed access denial instead of classifying only its innermost cause.
        $denied = $_.Exception
        while ($null -ne $denied -and $denied -isnot [UnauthorizedAccessException]) {
            $denied = $denied.InnerException
        }
        $result.status = if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) {
            'FILE_NOT_FOUND'
        } elseif ($null -ne $denied) { 'ACCESS_DENIED' }
        elseif ($cause -is [Text.DecoderFallbackException]) { 'MALFORMED_CONTENT' }
        else { 'READ_FAILED' }
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
    return $result
}

function Get-LocalDbLogMetadata {
    $profile = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrEmpty($profile)) { return @{ status = 'PROFILE_UNAVAILABLE'; files = @() } }
    $relative = 'Microsoft\Microsoft SQL Server Local DB\Instances\MSSQLLocalDB'
    $files = foreach ($name in 'error.log', 'error1.log', 'error2.log') {
        try {
            $path = Join-Path $profile "$relative\$name"
            $codes = Get-LocalDbLogErrorCodes $path
            if ($codes.status -eq 'FILE_NOT_FOUND') { continue }
            if ($codes.status -ne 'OK') {
                @{
                    location = "%LOCALAPPDATA%\$relative\$name"; status = $codes.status
                    bytesRead = $codes.bytesRead; truncated = $codes.truncated; sqlErrors = $codes.sqlErrors
                }
                continue
            }
            $file = Get-Item -LiteralPath $path -ErrorAction Stop
            @{
                location = "%LOCALAPPDATA%\$relative\$name"; status = 'OK'; sizeBytes = $file.Length
                createdUtc = $file.CreationTimeUtc.ToString('o'); modifiedUtc = $file.LastWriteTimeUtc.ToString('o')
                bytesRead = $codes.bytesRead; truncated = $codes.truncated; sqlErrors = $codes.sqlErrors
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
    $bootstrapErrors = 'None|AutomaticInstanceCreationFailed|AutomaticInstanceNotCreated|InstanceStartFailed|AccessDenied|LoginTimeout|ConnectionFailed|SqlClientError|LaunchFailed|AuthenticationFailed|TlsError|EngineUnavailable|InstanceCreationFailed|UnclassifiedError|OutputTruncated|ToolUnavailable|NativeProcessTimeout|NativeProbeFailed|NativeCommandFailed'
    $bootstrapPattern = '\A(info|start|sqlcmd),(-?\d{1,10}|unavailable),(' + $bootstrapErrors + '),(0x[0-9a-fA-F]{8}|None)\z'
    $bootstrap = @(foreach ($line in ($env:LAB_E02_BOOTSTRAP -split '\|', 4)) {
        if ($line -cmatch $bootstrapPattern) {
            @{
                stage = $Matches[1]; exitCode = if ($Matches[2] -ne 'unavailable') { [long]$Matches[2] }
                error = $Matches[3]; nativeCode = if ($Matches[4] -ne 'None') { $Matches[4] }
            }
        }
    })
    # This is a correlation basename only. Never open a caller-supplied receipt path.
    $bootstrapReceipt = if ($env:CI_LOCALDB_BOOTSTRAP_RECEIPT -cmatch '\Abootstrap-[a-f0-9]{32}\.json\z') {
        $env:CI_LOCALDB_BOOTSTRAP_RECEIPT
    }
    $report = [ordered]@{
        startedUtc = $startedUtc; capturedUtc = [DateTime]::UtcNow.ToString('o')
        status = if ($partial -or $partialLog -or $list.truncated -or $instance.truncated -or
            $errorKind -or !$instanceVersion -or !$state -or !$autoCreate) { 'PARTIAL' } else { 'OK' }
        sourceHeadSha = if ($env:LAB_E02_SOURCE_HEAD_SHA -cmatch '\A[a-f0-9]{40}\z') { $env:LAB_E02_SOURCE_HEAD_SHA }
        workflowSha = if ($env:GITHUB_SHA -cmatch '\A[a-f0-9]{40}\z') { $env:GITHUB_SHA }
        runId = if ($env:GITHUB_RUN_ID -cmatch '\A[0-9]{1,20}\z') { $env:GITHUB_RUN_ID }
        runAttempt = if ($env:GITHUB_RUN_ATTEMPT -cmatch '\A[0-9]{1,10}\z') { $env:GITHUB_RUN_ATTEMPT }
        job = if ($env:GITHUB_JOB -cmatch '\A[A-Za-z_][A-Za-z0-9_-]{0,99}\z') { $env:GITHUB_JOB }
        runner = @{
            osVersion = [Environment]::OSVersion.Version.ToString()
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            imageVersion = if ($env:ImageVersion -cmatch '^[0-9.]{1,32}$') { $env:ImageVersion }
        }
        bootstrap = @($bootstrap)
        bootstrapReceipt = $bootstrapReceipt
        bootstrapInvocation = if ($null -ne $bootstrapReceipt) { 'RECORDED' } else { 'NOT_RECORDED' }
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
