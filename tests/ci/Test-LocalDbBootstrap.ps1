[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '../../scripts/Initialize-LocalDb.ps1'
if (!(Test-Path -LiteralPath $helper)) { throw 'RED: LocalDB bootstrap helper is not implemented.' }
. $helper

# Only the external Windows/native boundary is simulated. The real state
# machine, classifiers, deadlines and evidence projection execute below.
$script:Scenario = ''
$script:State = ''
$script:Calls = [Collections.Generic.List[string]]::new()
$script:Queries = 0
$script:Passed = 0
function Assert-That([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Test-LocalDbHostedWindows { return $script:Scenario -ne 'NonHosted' }
function Start-Sleep([int]$Milliseconds) {
    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds $Milliseconds
    if ($script:Scenario -eq 'StoppedDuringBackoff' -and $Milliseconds -eq 2000) {
        $script:State = 'Stopped'
    }
}
function Get-LocalDbToolVersion([string]$Name) {
    if ($script:Scenario -eq 'MissingTool' -and $Name -eq 'SQLCMD.EXE') {
        return @{ status = 'COMMAND_NOT_FOUND' }
    }
    if ($script:Scenario -eq 'WrongDefaultTool' -and $Name -eq 'SqlLocalDB.exe') {
        return @{ status = 'OK'; executable = $Name; version = '2022.160.4382.1' }
    }
    return @{ status = 'OK'; executable = $Name; version = '2019.150.4382.1' }
}
function Native-Result([string]$Text, [int]$Code = 0, [string]$Status = 'OK') {
    return @{ status = $Status; exitCode = $Code; stdout = $Text; stderr = ''; truncated = $false }
}
function Invoke-LocalDbProbe {
    param([string]$Executable, [string[]]$Arguments, [int]$TimeoutMilliseconds,
        [bool]$KillEntireProcessTree = $true, [Diagnostics.Stopwatch]$DeadlineWatch,
        [long]$DeadlineMilliseconds)
    Assert-That (!$KillEntireProcessTree) 'Bootstrap must not kill a child SQL engine.'
    Assert-That ($TimeoutMilliseconds -gt 0 -and $TimeoutMilliseconds -le 60000) 'Native timeout must be bounded.'
    if ($script:Scenario -eq 'SharedDeadline') {
        Assert-That ($null -ne $DeadlineWatch -and $DeadlineMilliseconds -gt 0) 'Shared monotonic deadline missing.'
    }
    $verb = if ($Executable -ieq 'SQLCMD.EXE') { 'readiness' } else { $Arguments[0] }
    $script:Calls.Add($verb)
    if ($script:Scenario -eq 'Deadline') {
        Start-Sleep -Milliseconds ($TimeoutMilliseconds + 30)
        return @{ status = 'TIMEOUT'; exitCode = $null; stdout = ''; stderr = ''; truncated = $false }
    }
    switch ($verb) {
        'versions' {
            Assert-That ($Arguments.Count -eq 1) 'Installed runtime inspection accepts no target.'
            if ($script:Scenario -eq 'UnregisteredEngine') { return Native-Result 'Microsoft SQL Server 2022 (16.0.1000.1)' }
            if ($script:Scenario -eq 'UnknownVersions') { return Native-Result 'Unknown runtime TOP_SECRET_SENTINEL' }
            if ($script:Scenario -eq 'MultipleDefaultRuntimes') { return Native-Result "Microsoft SQL Server 2019 (15.0.4382.1)`nMicrosoft SQL Server 2022 (16.0.1000.1)" }
            return Native-Result 'Microsoft SQL Server 2019 (15.0.4382.1)'
        }
        'info' {
            Assert-That ($Arguments.Count -eq 2 -and $Arguments[1] -ceq 'MSSQLLocalDB') 'Foreign inspect target.'
            if ($script:Scenario -eq 'DeniedInfo') {
                return Native-Result 'Access is denied. TOP_SECRET_SENTINEL' 1 'COMMAND_FAILED'
            }
            if ($script:State -eq 'Missing') {
                if ($script:Scenario -eq 'ForeignMissing') { return Native-Result 'The automatic instance "OtherInstance" is not created.' }
                if ($script:Scenario -eq 'MixedMissing') {
                    return Native-Result "The automatic instance `"MSSQLLocalDB`" is not created.`nUnknown failure: TOP_SECRET_SENTINEL" 1 'COMMAND_FAILED'
                }
                return Native-Result 'The automatic instance "MSSQLLocalDB" is not created.'
            }
            if ($script:Scenario -eq 'MalformedInfo') { return Native-Result 'unrecognized successful output' }
            $version = if ($script:Scenario -eq 'WrongVersion') { '16.0.1000.1' } else { '15.0.4382.1' }
            $name = if ($script:Scenario -eq 'ForeignMetadata') { 'AnotherInstance' } else { 'MSSQLLocalDB' }
            $text = "Name: $name`nVersion: $version`nOwner: TOP_SECRET_SENTINEL`nAuto-create: Yes`nState: $script:State`n"
            if ($script:Scenario -eq 'NativeDoubleCr') { $text = $text.Replace("`n", "`r`r`n") }
            if ($script:Scenario -eq 'ConflictingMetadata') { $text += "State: Stopped`n" }
            return Native-Result $text
        }
        'create' {
            Assert-That (($Arguments -join '|') -ceq 'create|MSSQLLocalDB') 'Automatic creation must use the verified default runtime.'
            Assert-That ($script:State -eq 'Missing') 'Existing instance must not be recreated.'
            if ($script:Scenario -eq 'SlowColdCreate' -and $TimeoutMilliseconds -lt 20000) {
                return @{ status = 'TIMEOUT'; exitCode = $null; stdout = ''; stderr = ''; truncated = $false }
            }
            if ($script:Scenario -eq 'MissingEngine') {
                return Native-Result 'The specified LocalDB version is not installed.' 1 'COMMAND_FAILED'
            }
            if ($script:Scenario -eq 'NativeUnavailableVersion') {
                return Native-Result "Creation of LocalDB instance `"MSSQLLocalDB`" failed because of the following error:`r`nThe specified Local Database version is not available on this computer."
            }
            if ($script:Scenario -eq 'NativeInvalidParameter') {
                return Native-Result "Creation of LocalDB instance `"MSSQLLocalDB`" failed because of the following error:`nThe parameter for the Local Database Instance API method is incorrect. Consult the API documentation."
            }
            if ($script:Scenario -eq 'NativeWin32InvalidParameter') {
                return Native-Result "Creation of LocalDB instance `"MSSQLLocalDB`" failed because of the following error:`nThe parameter is incorrect."
            }
            if ($script:Scenario -eq 'CreateFailureZero') {
                return Native-Result 'Cannot create an automatic instance. TOP_SECRET_SENTINEL'
            }
            $script:State = 'Stopped'
            if ($script:Scenario -eq 'CreateInterrupted') { return Native-Result 'Engine initialization interrupted.' }
            if ($script:Scenario -eq 'FullCreateVersion') { return Native-Result 'LocalDB instance "MSSQLLocalDB" created with version 15.0.4382.1.' }
            return Native-Result 'LocalDB instance "MSSQLLocalDB" created with version 15.0.'
        }
        'start' {
            Assert-That ($script:State -eq 'Stopped') 'Cold instance must be explicitly created before start.'
            if ($script:Scenario -eq 'StartFailureZero') {
                return Native-Result 'Start of LocalDB instance "MSSQLLocalDB" failed because of the following error: Cannot create an automatic instance.'
            }
            if ($script:Scenario -eq 'StartNonzero') { return Native-Result '' 7 'COMMAND_FAILED' }
            if ($script:Scenario -ne 'StoppedAfterStart') { $script:State = 'Running' }
            if ($script:Scenario -eq 'StartInterrupted') { return Native-Result 'Engine initialization interrupted.' }
            return Native-Result 'LocalDB instance "MSSQLLocalDB" started.'
        }
        'readiness' {
            $script:Queries++
            Assert-That ($script:State -eq 'Running') 'Readiness cannot run before verified Running state.'
            Assert-That ($Arguments[1] -ceq '(localdb)\MSSQLLocalDB') 'Readiness target must stay local.'
            Assert-That ($Arguments -contains '-E' -and $Arguments -contains '-b') 'Authentication/SQL error gate missing.'
            Assert-That ($Arguments -contains '-l' -and $Arguments -contains '-t') 'SQL timeouts missing.'
            Assert-That ($Arguments -contains 'master' -and $Arguments[-1] -match 'IsLocalDB') 'Readiness must verify LocalDB identity in master.'
            if ($script:Scenario -in 'RetryThenReady','Exhausted','StoppedAfterTimeout','MixedTimeout','UnknownMixedTimeout','StoppedDuringBackoff') {
                if ($script:Scenario -ne 'RetryThenReady' -or $script:Queries -eq 1) {
                    $text = 'Sqlcmd: Error: Microsoft ODBC Driver 17 for SQL Server : Login timeout expired.'
                    if ($script:Scenario -eq 'StoppedAfterTimeout') { $script:State = 'Stopped' }
                    if ($script:Scenario -eq 'MixedTimeout') { $text += "`nCannot create an automatic instance. Access is denied. TOP_SECRET_SENTINEL" }
                    if ($script:Scenario -eq 'UnknownMixedTimeout') { $text += "`nUnrecognized native failure: TOP_SECRET_SENTINEL" }
                    return Native-Result $text 1 'COMMAND_FAILED'
                }
            }
            if ($script:Scenario -eq 'WrongReadiness') { return Native-Result '0' }
            if ($script:Scenario -eq 'QueryFailureZero') { return Native-Result 'Sqlcmd: Error: Login failed for user TOP_SECRET_SENTINEL.' }
            if ($script:Scenario -eq 'Truncated') {
                $r = Native-Result '1'; $r.truncated = $true; return $r
            }
            return Native-Result "1`r`n"
        }
        default { throw "Unexpected native operation: $verb" }
    }
}
function Run-Case([string]$Name, [string]$Initial, [string]$Want, [string]$Failure = '',
    [string]$Instance = 'MSSQLLocalDB', [int]$Deadline = 60, [bool]$WarmOnly = $false) {
    $script:Scenario = $Name; $script:State = $Initial; $script:Queries = 0; $script:Calls.Clear()
    $parameters = @{ InstanceName = $Instance; DeadlineSeconds = $Deadline }
    if ($WarmOnly) { $parameters.RequireWarm = $true }
    $result = Initialize-LocalDb @parameters
    Assert-That ($result.status -ceq $Want) "$Name expected $Want, got $($result.status) / $($result.failure)."
    if ($Failure) { Assert-That ($result.failure -ceq $Failure) "$Name expected failure $Failure, got $($result.failure)." }
    Assert-That (($result | ConvertTo-Json -Depth 12) -notmatch 'TOP_SECRET_SENTINEL') "$Name leaked native private output."
    Assert-That ($script:Queries -le 3) "$Name exceeded the readiness attempt limit."
    if ($Want -eq 'FAILED' -and $Name -notin 'Deadline','Exhausted') {
        Assert-That ($script:Queries -le 1) "$Name retried an unclassified or fatal failure."
    }
    $script:Passed++; Write-Host "PASS $Name"
    return $result
}

$cold = Run-Case 'Cold' 'Missing' 'READY'
Assert-That (($script:Calls -join ',') -ceq 'info,versions,create,info,start,info,readiness') 'Cold sequence must verify registered runtime before creation.'
Assert-That ($cold.mode -ceq 'COLD' -and $cold.attempts.Count -eq 7) 'Cold history incomplete.'
Assert-That ($cold.installedVersions -contains '15.0.4382.1') 'Installed runtime version was not retained.'
$slowCold = Run-Case 'SlowColdCreate' 'Missing' 'READY'
Assert-That (@($slowCold.attempts | Where-Object stage -eq 'create').Count -eq 1) 'Slow cold creation was retried.'
$warm = Run-Case 'Warm' 'Running' 'READY'
Assert-That (($script:Calls -join ',') -ceq 'info,readiness') 'Warm instance was changed.'
Assert-That ($warm.mode -ceq 'WARM') 'Warm receipt is not classified.'
$null = Run-Case 'ExistingStopped' 'Stopped' 'READY'
$null = Run-Case 'NonHosted' 'Missing' 'FAILED' 'HostedWindowsRequired'
Assert-That ($script:Calls.Count -eq 0) 'Non-hosted environment ran a native command.'
$null = Run-Case 'ForeignTarget' 'Missing' 'FAILED' 'ForeignInstanceDenied' 'UserInstance'
Assert-That ($script:Calls.Count -eq 0) 'Foreign target ran a native command.'
$null = Run-Case 'MissingTool' 'Missing' 'FAILED' 'ToolUnavailable'
Assert-That ($script:Calls.Count -eq 0) 'Missing sqlcmd was detected only after changing an instance.'
$null = Run-Case 'MissingEngine' 'Missing' 'FAILED' 'EngineUnavailable'
$null = Run-Case 'UnregisteredEngine' 'Missing' 'FAILED' 'EngineUnavailable'
Assert-That (($script:Calls -join ',') -ceq 'info,versions') 'Unregistered runtime caused an instance mutation.'
$null = Run-Case 'UnknownVersions' 'Missing' 'FAILED' 'InstanceMetadataMismatch'
$null = Run-Case 'WrongDefaultTool' 'Missing' 'FAILED' 'VersionMismatch'
Assert-That (($script:Calls -join ',') -ceq 'info,versions') 'Wrong default tool caused an instance mutation.'
$null = Run-Case 'MultipleDefaultRuntimes' 'Missing' 'FAILED' 'InstanceMetadataMismatch'
Assert-That (($script:Calls -join ',') -ceq 'info,versions') 'Ambiguous default runtime caused an instance mutation.'
$null = Run-Case 'NativeUnavailableVersion' 'Missing' 'FAILED' 'EngineUnavailable'
$invalidParameter = Run-Case 'NativeInvalidParameter' 'Missing' 'FAILED' 'InstanceCreationFailed'
Assert-That ($invalidParameter.attempts[-1].nativeDetail -ceq 'LocalDbApiInvalidParameter') 'Specific native reason was hidden by its generic creation header.'
$win32Parameter = Run-Case 'NativeWin32InvalidParameter' 'Missing' 'FAILED' 'InstanceCreationFailed'
Assert-That ($win32Parameter.attempts[-1].nativeDetail -ceq 'Win32InvalidParameter') 'Windows error was attributed to the LocalDB API.'
$null = Run-Case 'DeniedInfo' 'Stopped' 'FAILED' 'AccessDenied'
$null = Run-Case 'WrongVersion' 'Stopped' 'FAILED' 'VersionMismatch'
$null = Run-Case 'ForeignMetadata' 'Stopped' 'FAILED' 'InstanceMetadataMismatch'
$null = Run-Case 'MalformedInfo' 'Stopped' 'FAILED' 'InstanceMetadataMismatch'
$null = Run-Case 'ForeignMissing' 'Missing' 'FAILED' 'UnclassifiedError'
Assert-That (($script:Calls -join ',') -ceq 'info') 'Foreign uncreated-instance output issued a mutation.'
$null = Run-Case 'MixedMissing' 'Missing' 'FAILED' 'UnclassifiedError'
Assert-That (($script:Calls -join ',') -ceq 'info') 'Unknown extra info error issued a mutation.'
$null = Run-Case 'NativeDoubleCr' 'Running' 'READY'
$null = Run-Case 'SharedDeadline' 'Running' 'READY'
$null = Run-Case 'ConflictingMetadata' 'Running' 'FAILED' 'InstanceMetadataMismatch'
$null = Run-Case 'FullCreateVersion' 'Missing' 'READY'
$null = Run-Case 'CreateInterrupted' 'Missing' 'FAILED' 'UnclassifiedError'
$null = Run-Case 'StartInterrupted' 'Stopped' 'FAILED' 'UnclassifiedError'
$null = Run-Case 'WarmRequired' 'Running' 'READY' '' 'MSSQLLocalDB' 60 $true
$null = Run-Case 'WarmMissing' 'Missing' 'FAILED' 'WarmInstanceRequired' 'MSSQLLocalDB' 60 $true
Assert-That (($script:Calls -join ',') -ceq 'info') 'Warm check created an absent instance.'
$null = Run-Case 'WarmStopped' 'Stopped' 'FAILED' 'WarmInstanceRequired' 'MSSQLLocalDB' 60 $true
Assert-That (($script:Calls -join ',') -ceq 'info') 'Warm check restarted an instance.'
$null = Run-Case 'CreateFailureZero' 'Missing' 'FAILED' 'AutomaticInstanceCreationFailed'
$null = Run-Case 'StartFailureZero' 'Stopped' 'FAILED' 'AutomaticInstanceCreationFailed'
$null = Run-Case 'StartNonzero' 'Stopped' 'FAILED' 'NativeCommandFailed'
$null = Run-Case 'StoppedAfterStart' 'Stopped' 'FAILED' 'InstanceNotRunning'
$retry = Run-Case 'RetryThenReady' 'Running' 'READY'
Assert-That ($script:Queries -eq 2) 'Classified readiness timeout did not retry once.'
Assert-That (@($retry.attempts | Where-Object error -eq 'LoginTimeout').Count -eq 1) 'Recovered timeout history was lost.'
$null = Run-Case 'Exhausted' 'Running' 'FAILED' 'ReadinessAttemptsExhausted'
Assert-That ($script:Queries -eq 3) 'Readiness retries are not bounded at three attempts.'
$null = Run-Case 'MixedTimeout' 'Running' 'FAILED' 'AccessDenied'
$null = Run-Case 'UnknownMixedTimeout' 'Running' 'FAILED' 'UnclassifiedError'
$null = Run-Case 'StoppedAfterTimeout' 'Running' 'FAILED' 'InstanceNotRunning'
$null = Run-Case 'StoppedDuringBackoff' 'Running' 'FAILED' 'InstanceNotRunning'
$null = Run-Case 'QueryFailureZero' 'Running' 'FAILED' 'AuthenticationFailed'
$null = Run-Case 'WrongReadiness' 'Running' 'FAILED' 'ReadinessResponseMismatch'
$null = Run-Case 'Truncated' 'Running' 'FAILED' 'OutputTruncated'
$deadline = Run-Case 'Deadline' 'Stopped' 'FAILED' 'DeadlineExceeded' 'MSSQLLocalDB' 1
Assert-That ($script:Calls.Count -eq 1 -and $deadline.elapsedMilliseconds -lt 2000) 'Deadline issued extra operations or exceeded its bound.'

$identity = @{
    CI_LOCALDB_SOURCE_HEAD_SHA = ('a' * 40); GITHUB_SHA = ('b' * 40)
    GITHUB_RUN_ID = '12345'; GITHUB_RUN_ATTEMPT = '2'; GITHUB_JOB = 'core_sql'
}
$saved = @{}
$outputFile = Join-Path ([IO.Path]::GetTempPath()) ('localdb-output-' + [Guid]::NewGuid().ToString('N'))
$reportPath = $null
try {
    foreach ($key in @($identity.Keys) + @('GITHUB_OUTPUT')) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    }
    foreach ($key in $identity.Keys) { [Environment]::SetEnvironmentVariable($key, $identity[$key]) }
    $env:GITHUB_OUTPUT = $outputFile
    $script:Scenario = 'Warm'; $script:State = 'Running'; $script:Calls.Clear()
    $sourceBound = Initialize-LocalDb
    $reportPath = Write-LocalDbBootstrapReport $sourceBound
    Write-LocalDbBootstrapOutputs $sourceBound $reportPath
    $savedReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Assert-That ($savedReport.sourceHeadSha -ceq ('a' * 40) -and $savedReport.workflowSha -ceq ('b' * 40)) 'Receipt lost source/workflow distinction.'
    Assert-That ($savedReport.job -ceq 'core_sql' -and $savedReport.runId -ceq '12345' -and $savedReport.runAttempt -ceq '2') 'Receipt cannot identify its job and attempt.'
    $outputs = Get-Content -LiteralPath $outputFile
    Assert-That ($outputs -contains ('receipt=' + [IO.Path]::GetFileName($reportPath))) 'Receipt output is missing.'
    Assert-That ($outputs -contains 'bootstrap=info,0,None,None|sqlcmd,0,None,None') 'Legacy bootstrap projection changed.'
    $script:Passed++; Write-Host 'PASS Source-bound success receipt and legacy outputs'
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    if ($reportPath) { [IO.File]::Delete($reportPath) }
    [IO.File]::Delete($outputFile)
}

# Exercise the actual CLI failure path in an isolated copy. A denied foreign
# target cannot touch LocalDB, including on a real Windows CI worker.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('localdb-cli-' + [Guid]::NewGuid().ToString('N'))
$process = $null
try {
    $fixtureScripts = Join-Path $fixtureRoot 'scripts'
    [IO.Directory]::CreateDirectory($fixtureScripts) | Out-Null
    Copy-Item -LiteralPath $helper -Destination $fixtureScripts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../scripts/Collect-LocalDbDiagnostics.ps1') -Destination $fixtureScripts
    $shell = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $start = [Diagnostics.ProcessStartInfo]::new($shell)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',
        (Join-Path $fixtureScripts 'Initialize-LocalDb.ps1'),'-InstanceName','FOREIGN_TOP_SECRET_SENTINEL')) {
        $start.ArgumentList.Add($argument)
    }
    foreach ($key in $identity.Keys) { $start.Environment[$key] = $identity[$key] }
    $cliOutput = Join-Path $fixtureRoot 'outputs.txt'; $start.Environment['GITHUB_OUTPUT'] = $cliOutput
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(15000)) { throw 'Denied CLI did not return within its test deadline.' }
    $console = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    Assert-That ($process.ExitCode -eq 1) 'Denied CLI did not fail the step.'
    Assert-That ($console -notmatch 'TOP_SECRET_SENTINEL') 'Denied CLI printed the untrusted target.'
    $files = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'TestResults/LocalDbBootstrap') -File)
    Assert-That ($files.Count -eq 1) 'Failed invocation did not preserve exactly one receipt.'
    $failed = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
    Assert-That ($failed.status -ceq 'FAILED' -and $failed.failure -ceq 'ForeignInstanceDenied') 'Failed receipt lost its original cause.'
    Assert-That ($failed.attempts.Count -eq 0 -and $failed.sourceHeadSha -ceq ('a' * 40)) 'Denied target executed work or lost provenance.'
    Assert-That ((Get-Content -LiteralPath $cliOutput) -contains ('receipt=' + $files[0].Name)) 'Failure collector cannot link its invocation.'
    $script:Passed++; Write-Host 'PASS CLI failure exit, source receipt and invocation output'
} finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "LocalDB bootstrap regressions: $script:Passed passed."
$diagnosticsTests = Join-Path $PSScriptRoot 'Test-LocalDbDiagnostics.ps1'
if (Test-Path -LiteralPath $diagnosticsTests) {
    # Native subprocess tests must not inherit the orchestration boundary fakes.
    $shell = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    & $shell -NoLogo -NoProfile -NonInteractive -File $diagnosticsTests
    if ($LASTEXITCODE -ne 0) { throw 'LocalDB diagnostics subprocess regressions failed.' }
}
