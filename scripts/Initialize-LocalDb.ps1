[CmdletBinding()]
param(
    [string]$InstanceName = 'MSSQLLocalDB',
    [ValidateRange(1, 60)][int]$DeadlineSeconds = 60,
    [switch]$RequireWarm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Collect-LocalDbDiagnostics.ps1')

function Test-LocalDbHostedWindows {
    return $IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and
        $env:RUNNER_OS -ceq 'Windows' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted'
}

function Stop-LocalDbBootstrap([string]$Failure) {
    $exception = [InvalidOperationException]::new('LocalDB bootstrap failed.')
    $exception.Data['LocalDbFailure'] = $Failure
    throw $exception
}

function Get-LocalDbBootstrapError([string]$Text) {
    $Text = $Text -replace '\r+\n', "`n"
    # Fatal causes win even when a native tool also prints a login timeout.
    if ($Text -match 'access (?:is )?denied|permission denied|unauthorized') { return 'AccessDenied' }
    if ($Text -match 'login failed|cannot open database|\b18456\b') { return 'AuthenticationFailed' }
    if ($Text -match 'certificate|SSL Provider|TLS handshake|encryption error') { return 'TlsError' }
    if ($Text -match 'Cannot create an automatic instance') { return 'AutomaticInstanceCreationFailed' }
    if ($Text -match '(?:specified (?:LocalDB|Local Database) version|LocalDB runtime).*(?:not installed|not found|not available)') { return 'EngineUnavailable' }
    if ($Text -match 'Start of LocalDB instance .* failed|SQL Server process failed to start') { return 'InstanceStartFailed' }
    if ($Text -match 'Creation of LocalDB instance .* failed') { return 'InstanceCreationFailed' }
    if ($Text.Trim() -ceq 'The automatic instance "MSSQLLocalDB" is not created.') { return 'AutomaticInstanceNotCreated' }
    if ($Text -match 'automatic instance .* is not created') { return 'UnclassifiedError' }
    if ($Text -match 'Login timeout expired') {
        # A bare error 50, a creation failure, or an unknown second error is not
        # evidence of first-connection timing. Allow only the known timeout line.
        $lines = @($Text -split '\r?\n' | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
        $other = @($lines | Where-Object {
            $_.Trim() -notmatch '^(?:Sqlcmd: Error: Microsoft ODBC Driver (?:17|18) for SQL Server : )?Login timeout expired\.?$'
        })
        if ($other.Count -eq 0) { return 'LoginTimeout' }
        return 'UnclassifiedError'
    }
    if ($Text -match '(?i)\berror\b|\bfailed\b|\bfailure\b') { return 'UnclassifiedError' }
    return 'None'
}

function Get-LocalDbNativeDetail([string]$Text) {
    # Match only fixed cause categories. Never retain the raw native message,
    # which can include user profile, registry or instance directory paths.
    switch -Regex ($Text) {
        '(?:LocalDB|Local Database) version.*(?:not available|not installed)' { return 'VersionUnavailable' }
        '(?:(?:LocalDB|Local Database) version.*invalid|invalid.*(?:LocalDB|Local Database|version))' { return 'InvalidVersion' }
        'version available.*lower than.*requested' { return 'VersionTooLow' }
        'instance with the same name already exists.*lower version' { return 'ExistingLowerVersion' }
        'The parameter for the (?:Local Database|LocalDB) Instance API method is incorrect\.' { return 'LocalDbApiInvalidParameter' }
        '(?im)^\s*The parameter is incorrect\.\s*$' { return 'Win32InvalidParameter' }
        'parameter.*(?:incorrect|invalid)' { return 'InvalidParameter' }
        'instance name.*invalid' { return 'InvalidInstanceName' }
        '(?:automatic instance.*version|version.*automatic instance)' { return 'AutomaticInstanceVersionConstraint' }
        '(?:instance configuration.*corrupt|configuration.*instance.*corrupt)' { return 'InstanceConfigurationCorrupt' }
        'SQL Server process failed to start' { return 'SqlServerStartupFailed' }
        '(?:user.?profile|user profile).*folder' { return 'UserProfileFolder' }
        '(?:instance.*folder|folder.*instance)' { return 'InstanceFolder' }
        '(?:instance.*registry|registry.*instance)' { return 'InstanceRegistry' }
        default { return 'Unclassified' }
    }
}

function Invoke-LocalDbBootstrapStage {
    param([hashtable]$Context, [string]$Stage, [string[]]$Arguments, [int]$Attempt = 1)
    $remaining = $Context.deadlineMilliseconds - $Context.watch.ElapsedMilliseconds
    if ($remaining -le 0) { Stop-LocalDbBootstrap 'DeadlineExceeded' }
    $limit = if ($Stage -in 'info', 'versions') { 3000 } elseif ($Stage -eq 'readiness') { 12000 } else { 15000 }
    $timeout = [int][Math]::Min($limit, $remaining)
    $executable = if ($Stage -eq 'readiness') { 'SQLCMD.EXE' } else { 'SqlLocalDB.exe' }
    $started = [DateTime]::UtcNow.ToString('o')
    # SqlLocalDB can spawn the SQL engine. A deadline owns the launcher only;
    # killing its entire process tree would be an unguarded engine shutdown.
    $native = Invoke-LocalDbProbe -Executable $executable -Arguments $Arguments `
        -TimeoutMilliseconds $timeout -KillEntireProcessTree:$false `
        -DeadlineWatch $Context.watch -DeadlineMilliseconds $Context.deadlineMilliseconds
    $native.stdout = $native.stdout -replace '\r+\n', "`n"
    $native.stderr = $native.stderr -replace '\r+\n', "`n"
    $text = $native.stdout + [Environment]::NewLine + $native.stderr
    $errorKind = Get-LocalDbBootstrapError $text
    if ($native.truncated) { $errorKind = 'OutputTruncated' }
    elseif ($native.status -eq 'ACCESS_DENIED') { $errorKind = 'AccessDenied' }
    elseif ($native.status -eq 'COMMAND_NOT_FOUND') { $errorKind = 'ToolUnavailable' }
    elseif ($native.status -eq 'TIMEOUT') { $errorKind = 'NativeProcessTimeout' }
    elseif ($native.status -notin 'OK', 'COMMAND_FAILED') { $errorKind = 'NativeProbeFailed' }
    elseif ($errorKind -eq 'None' -and ($native.exitCode -ne 0 -or $null -eq $native.exitCode)) {
        $errorKind = 'NativeCommandFailed'
    }
    if ($errorKind -eq 'None' -and $Stage -in 'create', 'start') {
        $expected = if ($Stage -eq 'create') {
            '^LocalDB instance "MSSQLLocalDB" created with version 15\.0(?:\.\d+){0,2}\.$'
        } else { '^LocalDB instance "MSSQLLocalDB" started\.$' }
        if ($native.stdout.Trim() -notmatch $expected -or $native.stderr.Trim()) {
            $errorKind = 'UnclassifiedError'
        }
    }
    $code = [regex]::Match($text, '(?i)\b0x[0-9a-f]{8}\b').Value
    $record = [ordered]@{
        stage = $Stage; attempt = $Attempt; startedUtc = $started
        elapsedMilliseconds = $Context.watch.ElapsedMilliseconds
        status = $native.status; exitCode = $native.exitCode; error = $errorKind
        nativeDetail = if ($errorKind -ne 'None') { Get-LocalDbNativeDetail $text } else { $null }
        nativeCode = if ($code) { $code } else { $null }
        truncated = [bool]$native.truncated
        deadlineExpired = $Context.watch.ElapsedMilliseconds -ge $Context.deadlineMilliseconds
    }
    $Context.history.Add([pscustomobject]$record)
    return @{ native = $native; record = $record }
}

function Assert-LocalDbBootstrapStage([hashtable]$Stage) {
    if ($Stage.record.deadlineExpired) { Stop-LocalDbBootstrap 'DeadlineExceeded' }
    if ($Stage.record.error -ne 'None') { Stop-LocalDbBootstrap $Stage.record.error }
    if ($Stage.native.status -ne 'OK' -or $Stage.native.exitCode -ne 0) {
        Stop-LocalDbBootstrap 'NativeCommandFailed'
    }
}

function Read-LocalDbBootstrapInstance([hashtable]$Stage) {
    Assert-LocalDbBootstrapStage $Stage
    $text = $Stage.native.stdout
    $fields = @{}
    foreach ($field in 'Name', 'Version', 'State', 'Auto-create') {
        $entries = [regex]::Matches($text, '(?im)^' + [regex]::Escape($field) + ':[ \t]*([^\r\n]*)$')
        if ($entries.Count -ne 1) { Stop-LocalDbBootstrap 'InstanceMetadataMismatch' }
        $fields[$field] = $entries[0].Groups[1].Value.Trim()
    }
    $name = $fields.Name; $version = $fields.Version; $state = $fields.State; $automatic = $fields['Auto-create']
    if ($name -cne 'MSSQLLocalDB' -or $version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or
        $state -notin 'Running', 'Stopped' -or $automatic -ine 'Yes') {
        Stop-LocalDbBootstrap 'InstanceMetadataMismatch'
    }
    if ($version -notmatch '^15\.0\.') { Stop-LocalDbBootstrap 'VersionMismatch' }
    return @{ state = $state; version = $version }
}

function Initialize-LocalDb {
    [CmdletBinding()]
    param([string]$InstanceName = 'MSSQLLocalDB', [ValidateRange(1, 60)][int]$DeadlineSeconds = 60,
        [switch]$RequireWarm)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $context = @{
        watch = $watch; deadlineMilliseconds = [long]$DeadlineSeconds * 1000
        history = [Collections.Generic.List[object]]::new()
    }
    $report = [ordered]@{
        schemaVersion = 'CI_LOCALDB_BOOTSTRAP_V1'; startedUtc = [DateTime]::UtcNow.ToString('o')
        status = 'FAILED'; failure = $null; mode = 'UNKNOWN'; instance = 'MSSQLLocalDB'
        expectedVersion = '15.0'; selectedVersion = $null; engineVersion = $null; installedVersions = @(); readinessAttempts = 0
        deadlineSeconds = $DeadlineSeconds; elapsedMilliseconds = 0; attempts = @(); requireWarm = [bool]$RequireWarm
        sourceHeadSha = if ($env:CI_LOCALDB_SOURCE_HEAD_SHA -cmatch '\A[a-f0-9]{40}\z') { $env:CI_LOCALDB_SOURCE_HEAD_SHA }
        workflowSha = if ($env:GITHUB_SHA -cmatch '\A[a-f0-9]{40}\z') { $env:GITHUB_SHA }
        runId = if ($env:GITHUB_RUN_ID -cmatch '\A[0-9]{1,20}\z') { $env:GITHUB_RUN_ID }
        runAttempt = if ($env:GITHUB_RUN_ATTEMPT -cmatch '\A[0-9]{1,10}\z') { $env:GITHUB_RUN_ATTEMPT }
        job = if ($env:GITHUB_JOB -cmatch '\A[A-Za-z_][A-Za-z0-9_-]{0,99}\z') { $env:GITHUB_JOB }
        imageVersion = if ($env:ImageVersion -cmatch '\A[0-9.]{1,32}\z') { $env:ImageVersion }
        tools = @{}
    }
    try {
        if ($InstanceName -cne 'MSSQLLocalDB') { Stop-LocalDbBootstrap 'ForeignInstanceDenied' }
        if (!(Test-LocalDbHostedWindows)) { Stop-LocalDbBootstrap 'HostedWindowsRequired' }
        foreach ($tool in 'SqlLocalDB.exe', 'SQLCMD.EXE') {
            $version = Get-LocalDbToolVersion $tool
            $report.tools[$tool] = $version
            if ($version.status -eq 'ACCESS_DENIED') { Stop-LocalDbBootstrap 'AccessDenied' }
            if ($version.status -ne 'OK') { Stop-LocalDbBootstrap 'ToolUnavailable' }
        }
        $info = Invoke-LocalDbBootstrapStage $context 'info' @('info', 'MSSQLLocalDB')
        if ($info.record.deadlineExpired) { Stop-LocalDbBootstrap 'DeadlineExceeded' }
        if ($info.record.error -eq 'AutomaticInstanceNotCreated' -and
            $info.native.status -in 'OK', 'COMMAND_FAILED') {
            if ($RequireWarm) { Stop-LocalDbBootstrap 'WarmInstanceRequired' }
            $report.mode = 'COLD'
            # An executable file version does not prove a runtime is registered.
            # Inspect the read-only runtime inventory before any cold mutation.
            $versions = Invoke-LocalDbBootstrapStage $context 'versions' @('versions')
            Assert-LocalDbBootstrapStage $versions
            $installed = @(foreach ($line in ($versions.native.stdout -split "`n")) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $match = [regex]::Match($line.Trim(), '\AMicrosoft SQL Server [0-9]{4} \(([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)\)\z')
                if (!$match.Success) { Stop-LocalDbBootstrap 'InstanceMetadataMismatch' }
                $match.Groups[1].Value
            })
            $report.installedVersions = $installed
            $matching = @($installed | Where-Object { $_ -match '\A15\.0\.' })
            if ($matching.Count -eq 0) { Stop-LocalDbBootstrap 'EngineUnavailable' }
            if ($matching.Count -ne 1 -or $installed.Count -ne 1) { Stop-LocalDbBootstrap 'InstanceMetadataMismatch' }
            # Use the documented automatic-instance command only after proving
            # the tool default and sole registered runtime are the expected 15.0.
            $report.selectedVersion = $matching[0]
            $toolVersion = [regex]::Match($report.tools['SqlLocalDB.exe'].version,
                '\A(?:2019\.150|15\.0)\.([0-9]+)\.([0-9]+)\z')
            if (!$toolVersion.Success -or
                "15.0.$($toolVersion.Groups[1].Value).$($toolVersion.Groups[2].Value)" -cne $report.selectedVersion) {
                Stop-LocalDbBootstrap 'VersionMismatch'
            }
            $create = Invoke-LocalDbBootstrapStage $context 'create' @('create', 'MSSQLLocalDB')
            Assert-LocalDbBootstrapStage $create
            $info = Invoke-LocalDbBootstrapStage $context 'info' @('info', 'MSSQLLocalDB')
        }
        $instance = Read-LocalDbBootstrapInstance $info
        if ($RequireWarm -and $instance.state -ine 'Running') { Stop-LocalDbBootstrap 'WarmInstanceRequired' }
        $report.engineVersion = $instance.version
        if ($report.mode -eq 'UNKNOWN') {
            $report.mode = if ($instance.state -ieq 'Running') { 'WARM' } else { 'STOPPED' }
        }
        if ($instance.state -ieq 'Stopped') {
            $start = Invoke-LocalDbBootstrapStage $context 'start' @('start', 'MSSQLLocalDB')
            Assert-LocalDbBootstrapStage $start
            $info = Invoke-LocalDbBootstrapStage $context 'info' @('info', 'MSSQLLocalDB')
            $instance = Read-LocalDbBootstrapInstance $info
        }
        if ($instance.state -ine 'Running') { Stop-LocalDbBootstrap 'InstanceNotRunning' }
        $query = "SET NOCOUNT ON; SELECT CASE WHEN CONVERT(int, SERVERPROPERTY('IsLocalDB')) = 1 AND CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) LIKE '15.0.%' AND DB_NAME() = 'master' THEN 1 ELSE 0 END;"
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $report.readinessAttempts = $attempt
            $ready = Invoke-LocalDbBootstrapStage $context 'readiness' @(
                '-S', '(localdb)\MSSQLLocalDB', '-d', 'master', '-E', '-b', '-l', '5', '-t', '5',
                '-h', '-1', '-W', '-Q', $query) $attempt
            if ($ready.record.deadlineExpired) { Stop-LocalDbBootstrap 'DeadlineExceeded' }
            if ($ready.record.error -eq 'None') {
                Assert-LocalDbBootstrapStage $ready
                if ($ready.native.stdout.Trim() -cne '1') { Stop-LocalDbBootstrap 'ReadinessResponseMismatch' }
                $report.status = 'READY'
                break
            }
            if ($ready.record.error -ne 'LoginTimeout') { Stop-LocalDbBootstrap $ready.record.error }
            if ($attempt -eq 3) { Stop-LocalDbBootstrap 'ReadinessAttemptsExhausted' }
            # A timeout is retryable only while the exact instance still runs.
            # Never restart it, retry create, or execute a business statement here.
            $info = Invoke-LocalDbBootstrapStage $context 'info' @('info', 'MSSQLLocalDB') $attempt
            $instance = Read-LocalDbBootstrapInstance $info
            if ($instance.state -ine 'Running') { Stop-LocalDbBootstrap 'InstanceNotRunning' }
            $remaining = $context.deadlineMilliseconds - $watch.ElapsedMilliseconds
            if ($remaining -le 2000) { Stop-LocalDbBootstrap 'DeadlineExceeded' }
            Start-Sleep -Milliseconds 2000
            $info = Invoke-LocalDbBootstrapStage $context 'info' @('info', 'MSSQLLocalDB') $attempt
            $instance = Read-LocalDbBootstrapInstance $info
            if ($instance.state -ine 'Running') { Stop-LocalDbBootstrap 'InstanceNotRunning' }
        }
    } catch {
        $report.failure = if ($_.Exception.Data.Contains('LocalDbFailure')) {
            $_.Exception.Data['LocalDbFailure']
        } else { 'UnexpectedBootstrapFailure' }
    } finally {
        $watch.Stop()
        $report.elapsedMilliseconds = $watch.ElapsedMilliseconds
        $report.attempts = $context.history.ToArray()
        $report['completedUtc'] = [DateTime]::UtcNow.ToString('o')
    }
    return $report
}

function Write-LocalDbBootstrapReport([System.Collections.IDictionary]$Report) {
    $root = Join-Path $PSScriptRoot '../TestResults/LocalDbBootstrap'
    for ($directory = [IO.DirectoryInfo]::new($root); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'LocalDB bootstrap evidence directory contains a reparse point.'
        }
    }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $path = Join-Path $root "bootstrap-$([Guid]::NewGuid().ToString('N')).json"
    $temporary = "$path.tmp"
    try {
        $Report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8
        [IO.File]::Move($temporary, $path)
    } finally { [IO.File]::Delete($temporary) }
    return $path
}

function Write-LocalDbBootstrapOutputs([System.Collections.IDictionary]$Report, [string]$ReportPath) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { return }
    $leaf = [IO.Path]::GetFileName($ReportPath)
    if ($leaf -cnotmatch '\Abootstrap-[a-f0-9]{32}\.json\z') { throw 'Invalid bootstrap receipt identity.' }
    $legacy = @(foreach ($stage in 'info', 'start', 'readiness') {
        $last = $Report.attempts | Where-Object stage -eq $stage | Select-Object -Last 1
        if ($null -eq $last) { continue }
        $label = if ($stage -eq 'readiness') { 'sqlcmd' } else { $stage }
        $exit = if ($null -eq $last.exitCode) { 'unavailable' } else { [string]$last.exitCode }
        $code = if ($last.nativeCode) { $last.nativeCode } else { 'None' }
        "$label,$exit,$($last.error),$code"
    })
    @("receipt=$leaf", "bootstrap=$($legacy -join '|')") | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = Initialize-LocalDb -InstanceName $InstanceName -DeadlineSeconds $DeadlineSeconds -RequireWarm:$RequireWarm
        $path = Write-LocalDbBootstrapReport $result
        Write-LocalDbBootstrapOutputs $result $path
        Write-Host "LocalDB bootstrap $($result.status); mode=$($result.mode); readinessAttempts=$($result.readinessAttempts); elapsedMs=$($result.elapsedMilliseconds); report=$([IO.Path]::GetFileName($path))"
        if ($result.status -ne 'READY') {
            Write-Host "LocalDB bootstrap failure: $($result.failure)."
            exit 1
        }
        exit 0
    } catch {
        Write-Host "LocalDB bootstrap evidence failure ($($_.Exception.GetBaseException().GetType().Name))."
        exit 1
    }
}
