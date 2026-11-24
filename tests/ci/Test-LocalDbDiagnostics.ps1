[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Collect-LocalDbDiagnostics.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('localdb-diagnostics-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$shell = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$failures = [Collections.Generic.List[string]]::new()
$passed = 0

function Assert-Diagnostics {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Test-DiagnosticsCase {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:passed++
        Write-Host "PASS $Name"
    } catch {
        $script:failures.Add($Name)
        Write-Host "FAIL $Name ($($_.Exception.GetBaseException().Message))"
    }
}

function Write-DiagnosticsFixture {
    param([string]$Name, [string]$Text, [Text.Encoding]$Encoding)
    $path = Join-Path $testRoot $Name
    [IO.File]::WriteAllText($path, $Text, $Encoding)
    return $path
}

function Assert-NumericDiagnosticsOnly {
    param($Result)
    foreach ($entry in $Result.sqlErrors) {
        Assert-Diagnostics ($entry.Count -eq 3) 'Each signature must contain only error, severity and state.'
        foreach ($field in 'error', 'severity', 'state') {
            Assert-Diagnostics ($entry[$field] -is [int]) 'A signature contains a non-integer field.'
        }
    }
    $json = $Result | ConvertTo-Json -Depth 5 -Compress
    Assert-Diagnostics ($json -notmatch 'private-sentinel|Password=|Bearer |localdb-diagnostics-|\.log') 'Diagnostics leaked source text or a private path.'
}

function Stop-OwnedDiagnosticsProcesses {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return }
    foreach ($owned in ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)) {
        $process = Get-Process -Id $owned.id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            if ($process.StartTime.ToUniversalTime().Ticks -eq $owned.started -and -not $process.HasExited) {
                $process.Kill($true)
                [void]$process.WaitForExit(1500)
            }
        } finally { $process.Dispose() }
    }
}

function Invoke-DiagnosticsReportFixture {
    param([hashtable]$Values)
    $directory = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
    $scripts = Join-Path $directory 'scripts'
    [IO.Directory]::CreateDirectory($scripts) | Out-Null
    $collector = Join-Path $scripts 'Collect-LocalDbDiagnostics.ps1'
    [IO.File]::Copy((Join-Path $PSScriptRoot '../../scripts/Collect-LocalDbDiagnostics.ps1'), $collector)
    $settings = @{}
    foreach ($name in 'LAB_E02_SOURCE_HEAD_SHA', 'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT',
        'GITHUB_JOB', 'LAB_E02_BOOTSTRAP', 'CI_LOCALDB_BOOTSTRAP_RECEIPT') { $settings[$name] = '' }
    foreach ($entry in $Values.GetEnumerator()) { $settings[$entry.Key] = $entry.Value }
    $encodedSettings = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Compress)))
    $setup = @'
. '__COLLECTOR__'
$settings = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__SETTINGS__')) | ConvertFrom-Json -AsHashtable
foreach ($entry in $settings.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
function Invoke-LocalDbProbe {
    param([string]$Executable, [string[]]$Arguments)
    $output = if ($Arguments.Count -eq 1) { 'MSSQLLocalDB' }
        else { "Name: MSSQLLocalDB`nVersion: 15.0.4382.1`nState: Stopped`nAuto-create: Yes" }
    @{ status = 'OK'; exitCode = 0; stdout = $output; stderr = ''; truncated = $false }
}
function Get-LocalDbToolVersion { param([string]$Name); @{ status = 'OK'; version = '15.0.4382.1' } }
function Get-LocalDbLogMetadata { @{ status = 'OK'; files = @() } }
function Get-Service { param($Name, $ErrorAction); $null }
function Get-Process { param($Name, $ErrorAction); @() }
function Get-WinEvent {
    param($FilterHashtable, $MaxEvents, $ErrorAction)
    [pscustomobject]@{ Id = 528; Level = 2; TimeCreated = [DateTime]::UtcNow; Message = 'private-sentinel' }
}
Write-LocalDbDiagnostics
'@
    $setup = $setup.Replace('__COLLECTOR__', $collector.Replace("'", "''")).Replace('__SETTINGS__', $encodedSettings)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($setup))
    $process = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) 10000
    Assert-Diagnostics ($process.status -eq 'OK' -and $process.exitCode -eq 0) 'The report fixture did not execute successfully.'
    $files = @([IO.Directory]::EnumerateFiles((Join-Path $directory 'TestResults/LAB-E02'), '*.json'))
    Assert-Diagnostics ($files.Count -eq 1) 'The fixture did not write exactly one diagnostic report.'
    $json = [IO.File]::ReadAllText($files[0])
    return @{ report = ($json | ConvertFrom-Json -AsHashtable); json = $json; console = $process.stdout + $process.stderr }
}

try {
    Test-DiagnosticsCase 'Native nonzero exit is captured from the actual process' {
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
            '[Console]::Out.Write("out-marker"); [Console]::Error.Write("err-marker"); exit 37') 6000
        Assert-Diagnostics ($result.status -eq 'COMMAND_FAILED' -and $result.exitCode -eq 37) 'Native failure was lost.'
        Assert-Diagnostics ($result.stdout -eq 'out-marker' -and $result.stderr -eq 'err-marker') 'One native output stream was lost.'
    }

    Test-DiagnosticsCase 'Native probe timeout remains bounded' {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30') 300
        Assert-Diagnostics ($result.status -eq 'TIMEOUT') 'A stalled native process did not time out.'
        Assert-Diagnostics ($watch.ElapsedMilliseconds -lt 3000) 'Native timeout exceeded its cleanup allowance.'
    }

    Test-DiagnosticsCase 'Local timeout includes executable discovery and refuses a late launch' {
        $marker = Join-Path $testRoot 'late-local-launch.txt'
        function Get-Command {
            param($Name, $CommandType, $ErrorAction)
            Start-Sleep -Milliseconds 2200
            Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
        }
        $command = "[IO.File]::WriteAllText('$($marker.Replace("'", "''"))', 'started')"
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $command) 2000
        Assert-Diagnostics ($result.status -eq 'TIMEOUT' -and $null -eq $result.exitCode) 'Delayed discovery did not consume the local budget.'
        Assert-Diagnostics (-not [IO.File]::Exists($marker)) 'A native process was launched after its local budget expired.'
    }

    Test-DiagnosticsCase 'Shared deadline can expire during discovery without launching a child' {
        $marker = Join-Path $testRoot 'late-shared-launch.txt'
        function Get-Command {
            param($Name, $CommandType, $ErrorAction)
            Start-Sleep -Milliseconds 500
            Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $command = "[IO.File]::WriteAllText('$($marker.Replace("'", "''"))', 'started')"
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $command) 2000 `
            -DeadlineWatch $watch -DeadlineMilliseconds 300
        Assert-Diagnostics ($result.status -eq 'TIMEOUT' -and $null -eq $result.exitCode) 'Delayed discovery ignored the shared deadline.'
        Assert-Diagnostics (-not [IO.File]::Exists($marker)) 'A native process was launched after the shared deadline expired.'
    }

    Test-DiagnosticsCase 'Shared deadline also bounds native stream waiting' {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30') 5000 `
            -DeadlineWatch $watch -DeadlineMilliseconds 500
        Assert-Diagnostics ($result.status -eq 'TIMEOUT' -and $watch.ElapsedMilliseconds -lt 2500) 'The stream loop ignored the shorter shared deadline.'
        Assert-Diagnostics ($null -eq $result.exitCode) 'A still-running launcher acquired an invented exit code.'
    }

    Test-DiagnosticsCase 'Both native streams are drained with bounded retention' {
        $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
            '[Console]::Out.Write("x" * 20000); [Console]::Error.Write("y" * 20000)') 6000
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.exitCode -eq 0) 'Draining bounded streams blocked the child.'
        Assert-Diagnostics ($result.truncated -and $result.stdout.Length -eq 8192 -and $result.stderr.Length -eq 8192) 'Native stream retention was not bounded.'
    }

    Test-DiagnosticsCase 'Launcher timeout can preserve its owned child process' {
        $ownedPath = Join-Path $testRoot 'owned-processes.json'
        $heartbeatPath = Join-Path $testRoot 'owned-heartbeat.txt'
        $childCode = @"
for (`$i = 0; `$i -lt 300; `$i++) {
    [IO.File]::WriteAllText('$($heartbeatPath.Replace("'", "''"))', [string]`$i)
    Start-Sleep -Milliseconds 100
}
"@
        $childEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
        $launcherCode = @"
`$child = [Diagnostics.Process]::new()
`$child.StartInfo.FileName = '$($shell.Replace("'", "''"))'
`$child.StartInfo.UseShellExecute = `$false
`$child.StartInfo.CreateNoWindow = `$true
`$child.StartInfo.RedirectStandardOutput = `$true
`$child.StartInfo.RedirectStandardError = `$true
foreach (`$arg in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', '$childEncoded')) {
    `$child.StartInfo.ArgumentList.Add(`$arg)
}
[void]`$child.Start()
@(@{ id = `$PID; started = (Get-Process -Id `$PID).StartTime.ToUniversalTime().Ticks },
    @{ id = `$child.Id; started = `$child.StartTime.ToUniversalTime().Ticks }) |
    ConvertTo-Json | Set-Content -LiteralPath '$($ownedPath.Replace("'", "''"))'
Start-Sleep -Seconds 30
"@
        $launcherEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launcherCode))
        try {
            $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $launcherEncoded) 4000 -KillEntireProcessTree:$false
            Assert-Diagnostics ($result.status -eq 'TIMEOUT') 'Launcher did not return the timeout status.'
            Assert-Diagnostics ([IO.File]::Exists($ownedPath) -and [IO.File]::Exists($heartbeatPath)) 'The owned child did not initialize.'
            $before = [IO.File]::ReadAllText($heartbeatPath)
            Start-Sleep -Milliseconds 350
            $after = [IO.File]::ReadAllText($heartbeatPath)
            Assert-Diagnostics ($before -ne $after) 'Launcher cleanup killed the child despite the opt-out.'
        } finally { Stop-OwnedDiagnosticsProcesses $ownedPath }
    }

    Test-DiagnosticsCase 'A pipe timeout retains the completed launcher exit code and preserves its child' {
        $ownedPath = Join-Path $testRoot 'inherited-pipe-processes.json'
        $heartbeatPath = Join-Path $testRoot 'inherited-pipe-heartbeat.txt'
        $childCode = @"
for (`$i = 0; `$i -lt 300; `$i++) {
    [IO.File]::WriteAllText('$($heartbeatPath.Replace("'", "''"))', [string]`$i)
    Start-Sleep -Milliseconds 100
}
"@
        $childEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
        $launcherCode = @"
`$child = [Diagnostics.Process]::new()
`$child.StartInfo.FileName = '$($shell.Replace("'", "''"))'
`$child.StartInfo.UseShellExecute = `$false
`$child.StartInfo.CreateNoWindow = `$true
foreach (`$arg in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', '$childEncoded')) {
    `$child.StartInfo.ArgumentList.Add(`$arg)
}
[void]`$child.Start()
@(@{ id = `$PID; started = (Get-Process -Id `$PID).StartTime.ToUniversalTime().Ticks },
    @{ id = `$child.Id; started = `$child.StartTime.ToUniversalTime().Ticks }) |
    ConvertTo-Json | Set-Content -LiteralPath '$($ownedPath.Replace("'", "''"))'
exit 37
"@
        $launcherEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launcherCode))
        try {
            $result = Invoke-LocalDbProbe $shell @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $launcherEncoded) 4000 -KillEntireProcessTree:$false
            Assert-Diagnostics ($result.status -eq 'TIMEOUT' -and $result.exitCode -eq 37) 'The exited launcher status was lost while its child held the pipes.'
            Assert-Diagnostics ([IO.File]::Exists($heartbeatPath)) 'The child holding the inherited pipes did not initialize.'
            $before = [IO.File]::ReadAllText($heartbeatPath)
            Start-Sleep -Milliseconds 350
            Assert-Diagnostics ($before -ne [IO.File]::ReadAllText($heartbeatPath)) 'A completed launcher caused its live child to be killed.'
        } finally { Stop-OwnedDiagnosticsProcesses $ownedPath }
    }

    Test-DiagnosticsCase 'Diagnostics correlate the safe receipt basename with job and legacy stages' {
        $receipt = 'bootstrap-' + ('c' * 32) + '.json'
        $fixture = Invoke-DiagnosticsReportFixture @{
            LAB_E02_SOURCE_HEAD_SHA = 'a' * 40; GITHUB_SHA = 'b' * 40; GITHUB_RUN_ID = '123'; GITHUB_RUN_ATTEMPT = '2'
            GITHUB_JOB = 'web_sales'; CI_LOCALDB_BOOTSTRAP_RECEIPT = $receipt
            LAB_E02_BOOTSTRAP = 'info,0,None,None|start,0,None,None|sqlcmd,1,LoginTimeout,0x80004005'
        }
        $report = $fixture.report
        Assert-Diagnostics ($report.job -eq 'web_sales' -and $report.bootstrapReceipt -eq $receipt -and $report.bootstrapInvocation -eq 'RECORDED') 'Receipt/job correlation was not recorded.'
        Assert-Diagnostics ($report.sourceHeadSha -eq ('a' * 40) -and $report.workflowSha -eq ('b' * 40) -and $report.runId -eq '123' -and $report.runAttempt -eq '2') 'Source/run correlation changed.'
        Assert-Diagnostics ($report.bootstrap.Count -eq 3 -and $report.bootstrap[2].stage -eq 'sqlcmd' -and $report.bootstrap[2].error -eq 'LoginTimeout' -and $report.bootstrap[2].exitCode -eq 1) 'Legacy stage evidence was not retained.'
        foreach ($entry in $report.applicationEvents.events) { Assert-Diagnostics ($entry.Count -eq 3) 'Event metadata changed shape.' }
        Assert-Diagnostics (($fixture.json + $fixture.console) -notmatch 'private-sentinel') 'Event payload escaped numeric metadata projection.'
    }

    Test-DiagnosticsCase 'Typed helper errors survive the explicit legacy-stage allowlist' {
        $errors = @('AuthenticationFailed', 'TlsError', 'EngineUnavailable', 'InstanceCreationFailed',
            'UnclassifiedError', 'OutputTruncated', 'ToolUnavailable', 'NativeProcessTimeout', 'NativeProbeFailed', 'NativeCommandFailed')
        for ($index = 0; $index -lt $errors.Count; $index += 3) {
            $batch = @($errors | Select-Object -Skip $index -First 3)
            $lines = for ($stage = 0; $stage -lt $batch.Count; $stage++) { "$(@('info', 'start', 'sqlcmd')[$stage]),unavailable,$($batch[$stage]),None" }
            $fixture = Invoke-DiagnosticsReportFixture @{ LAB_E02_BOOTSTRAP = $lines -join '|' }
            Assert-Diagnostics ($fixture.report.bootstrap.Count -eq $batch.Count) 'A helper failure class was dropped.'
            for ($stage = 0; $stage -lt $batch.Count; $stage++) {
                Assert-Diagnostics ($fixture.report.bootstrap[$stage].error -eq $batch[$stage]) 'A helper failure class was changed.'
            }
        }
    }

    Test-DiagnosticsCase 'Invalid metadata is rejected without following a receipt path' {
        $fixture = Invoke-DiagnosticsReportFixture @{
            LAB_E02_SOURCE_HEAD_SHA = ('a' * 40) + "`n"; GITHUB_SHA = 'private-sentinel-source'
            GITHUB_RUN_ID = "123`n"; GITHUB_RUN_ATTEMPT = "2`n"; GITHUB_JOB = '../private-sentinel-job'
            CI_LOCALDB_BOOTSTRAP_RECEIPT = '../bootstrap-' + ('c' * 32) + '.json'
            LAB_E02_BOOTSTRAP = 'delete,0,None,None|start,1,private-sentinel,None|sqlcmd,1,LoginTimeout,private-sentinel'
        }
        $report = $fixture.report
        Assert-Diagnostics ($null -eq $report.job -and $null -eq $report.bootstrapReceipt -and $report.bootstrapInvocation -eq 'NOT_RECORDED') 'Unsafe receipt/job metadata was retained.'
        Assert-Diagnostics ($null -eq $report.sourceHeadSha -and $null -eq $report.workflowSha -and $null -eq $report.runId -and $null -eq $report.runAttempt) 'Invalid source/run metadata was retained.'
        Assert-Diagnostics ($report.bootstrap.Count -eq 0) 'An unapproved stage or error escaped the allowlist.'
        Assert-Diagnostics (($fixture.json + $fixture.console) -notmatch 'private-sentinel|\.\./') 'An invalid metadata payload leaked.'
    }

    Test-DiagnosticsCase 'A missing invocation produces an explicit empty bootstrap record' {
        $fixture = Invoke-DiagnosticsReportFixture @{}
        Assert-Diagnostics ($fixture.report.bootstrapInvocation -eq 'NOT_RECORDED' -and $null -eq $fixture.report.bootstrapReceipt -and $fixture.report.bootstrap.Count -eq 0) 'A pre-helper guard failure has ambiguous bootstrap evidence.'
    }

    Test-DiagnosticsCase 'Receipt and job metadata reject trailing newlines' {
        $fixture = Invoke-DiagnosticsReportFixture @{
            GITHUB_JOB = "core_sql`n"; CI_LOCALDB_BOOTSTRAP_RECEIPT = 'bootstrap-' + ('c' * 32) + ".json`n"
        }
        Assert-Diagnostics ($null -eq $fixture.report.job -and $null -eq $fixture.report.bootstrapReceipt -and $fixture.report.bootstrapInvocation -eq 'NOT_RECORDED') 'An end-anchor accepted extra metadata characters.'
    }

    Test-DiagnosticsCase 'UTF-8 BOM log exposes numeric signatures only' {
        $text = "private-sentinel Password=secret`r`n2026-11-25 00:00:00.00 spid5 Error: 5179, Severity: 16, State: 1.`r`nBearer private-sentinel`r`nError: 17113, Severity: 16, State: 2.`r`n"
        $path = Write-DiagnosticsFixture 'utf8.log' $text ([Text.UTF8Encoding]::new($true))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.sqlErrors.Count -eq 2) 'UTF-8 signatures were not extracted.'
        Assert-Diagnostics ($result.sqlErrors[0].error -eq 5179 -and $result.sqlErrors[0].severity -eq 16 -and $result.sqlErrors[0].state -eq 1) 'First numeric signature changed.'
        Assert-Diagnostics ($result.sqlErrors[1].error -eq 17113 -and $result.sqlErrors[1].state -eq 2) 'Second numeric signature changed.'
        Assert-Diagnostics ($result.bytesRead -eq (Get-Item -LiteralPath $path).Length -and -not $result.truncated) 'A short complete read was misreported.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'UTF-16LE BOM log is decoded before numeric projection' {
        $path = Write-DiagnosticsFixture 'utf16.log' "private-sentinel 密碼`r`nError: 17058, Severity: 16, State: 1.`r`n" ([Text.UnicodeEncoding]::new($false, $true))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.sqlErrors.Count -eq 1 -and $result.sqlErrors[0].error -eq 17058) 'UTF-16LE signature was lost.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'A BOM-less UTF-8 log is supported' {
        $path = Write-DiagnosticsFixture 'plain.log' "Error: 5120, Severity: 16, State: 101.`n" ([Text.UTF8Encoding]::new($false))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.sqlErrors.Count -eq 1 -and $result.sqlErrors[0].state -eq 101) 'A BOM-less UTF-8 signature was lost.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Log tail and retained signatures have independent hard bounds' {
        $text = "Error: 999, Severity: 16, State: 1.`n" + ('private-sentinel 密碼' * 9000) + "`n"
        foreach ($number in 2001..2020) { $text += "Error: $number, Severity: 16, State: 1.`n" }
        $path = Write-DiagnosticsFixture 'large-utf8.log' $text ([Text.UTF8Encoding]::new($true))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.truncated -and $result.bytesRead -le 65536) 'The byte limit was exceeded or truncation was hidden.'
        Assert-Diagnostics ($result.sqlErrors.Count -eq 16 -and $result.sqlErrors[0].error -eq 2005 -and $result.sqlErrors[15].error -eq 2020) 'The latest sixteen signatures were not retained.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'UTF-16LE tail alignment discards a partial first line' {
        $text = ('private-sentinel 密碼🔒' * 4000) + "`r`nError: 574, Severity: 16, State: 1.`r`n"
        $path = Write-DiagnosticsFixture 'large-utf16.log' $text ([Text.UnicodeEncoding]::new($false, $true))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.truncated -and $result.bytesRead -le 65536) 'UTF-16LE tail was not decoded within the byte limit.'
        Assert-Diagnostics ($result.sqlErrors.Count -eq 1 -and $result.sqlErrors[0].error -eq 574) 'A complete signature after the partial UTF-16LE line was lost.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Signature overflow and malformed triples do not become diagnostic values' {
        $path = Write-DiagnosticsFixture 'invalid-triples.log' "Error: 9999999999, Severity: 16, State: 1.`nError: 12, Severity: secret, State: 1.`nError: 13, Severity: 16, State: 999.`nprivate-sentinel`n" ([Text.UTF8Encoding]::new($false))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'OK' -and $result.sqlErrors.Count -eq 0) 'An invalid numeric triple was retained.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Missing log returns typed status without exposing its path' {
        $result = Get-LocalDbLogErrorCodes (Join-Path $testRoot 'private-sentinel-missing.log')
        Assert-Diagnostics ($result.status -eq 'FILE_NOT_FOUND' -and $result.bytesRead -eq 0 -and $result.sqlErrors.Count -eq 0) 'A missing file was not classified.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Directory is rejected before any log read' {
        $result = Get-LocalDbLogErrorCodes $testRoot
        Assert-Diagnostics ($result.status -eq 'UNEXPECTED_FILE_TYPE' -and $result.bytesRead -eq 0) 'A directory was accepted as an error log.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Invalid UTF-8 is reported as malformed content' {
        $path = Join-Path $testRoot 'malformed-utf8.log'
        [IO.File]::WriteAllBytes($path, [byte[]]@(0xef, 0xbb, 0xbf, 0xc3, 0x28))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'MALFORMED_CONTENT' -and $result.sqlErrors.Count -eq 0) 'Malformed UTF-8 was silently accepted.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'An incomplete UTF-16LE code unit is reported as malformed content' {
        $path = Join-Path $testRoot 'malformed-utf16.log'
        [IO.File]::WriteAllBytes($path, [byte[]]@(0xff, 0xfe, 0x45))
        $result = Get-LocalDbLogErrorCodes $path
        Assert-Diagnostics ($result.status -eq 'MALFORMED_CONTENT' -and $result.sqlErrors.Count -eq 0) 'Incomplete UTF-16LE was silently accepted.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'A log file reparse point is rejected before reading' {
        $target = Write-DiagnosticsFixture 'link-target.log' "Error: 5120, Severity: 16, State: 1.`n" ([Text.UTF8Encoding]::new($false))
        $link = Join-Path $testRoot 'file-link.log'
        [IO.File]::CreateSymbolicLink($link, $target) | Out-Null
        $result = Get-LocalDbLogErrorCodes $link
        Assert-Diagnostics ($result.status -eq 'REPARSE_POINT' -and $result.bytesRead -eq 0) 'A linked file was read.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'A reparse point ancestor is rejected before reading' {
        $target = Join-Path $testRoot 'real-directory'
        [IO.Directory]::CreateDirectory($target) | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'error.log'), "Error: 5120, Severity: 16, State: 1.`n")
        $link = Join-Path $testRoot 'directory-link'
        [IO.Directory]::CreateSymbolicLink($link, $target) | Out-Null
        $result = Get-LocalDbLogErrorCodes (Join-Path $link 'error.log')
        Assert-Diagnostics ($result.status -eq 'REPARSE_POINT' -and $result.bytesRead -eq 0) 'A log under a linked directory was read.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'Denied synthetic log returns access status without payload' {
        $path = Write-DiagnosticsFixture 'denied.log' 'private-sentinel' ([Text.UTF8Encoding]::new($false))
        if ($IsWindows) {
            $original = Get-Acl -LiteralPath $path
            $restricted = Get-Acl -LiteralPath $path
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.WindowsIdentity]::GetCurrent().User,
                [Security.AccessControl.FileSystemRights]::ReadData,
                [Security.AccessControl.AccessControlType]::Deny)
            $restricted.AddAccessRule($rule)
            try {
                Set-Acl -LiteralPath $path -AclObject $restricted
                $result = Get-LocalDbLogErrorCodes $path
            } finally { Set-Acl -LiteralPath $path -AclObject $original }
        } else {
            $original = [IO.File]::GetUnixFileMode($path)
            try {
                [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::None)
                $result = Get-LocalDbLogErrorCodes $path
            } finally { [IO.File]::SetUnixFileMode($path, $original) }
        }
        Assert-Diagnostics ($result.status -eq 'ACCESS_DENIED' -and $result.sqlErrors.Count -eq 0) 'Permission denial was not classified.'
        Assert-NumericDiagnosticsOnly $result
    }

    Test-DiagnosticsCase 'A sharing violation returns read failure without payload' {
        $path = Write-DiagnosticsFixture 'locked.log' 'private-sentinel' ([Text.UTF8Encoding]::new($false))
        $locked = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try { $result = Get-LocalDbLogErrorCodes $path }
        finally { $locked.Dispose() }
        Assert-Diagnostics ($result.status -eq 'READ_FAILED' -and $result.sqlErrors.Count -eq 0) 'Sharing failure was not classified.'
        Assert-NumericDiagnosticsOnly $result
    }
} finally {
    if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}

if ($failures.Count -gt 0) { throw "LocalDB diagnostics regressions failed: $($failures -join ', ')" }
Write-Host "LocalDB diagnostics regressions passed: $passed."
