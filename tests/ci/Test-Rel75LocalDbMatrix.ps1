[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Invoke-Rel75LocalDbSample.ps1')

$script:passed = 0
function Assert-Rel75Test([bool]$Condition,[string]$Message) {
    if (!$Condition) { throw $Message }
}
function Expect-Rel75Failure([scriptblock]$Action,[string]$Code) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Data['Rel75Code'] -cne $Code) { throw }
        $script:passed++
        return
    }
    throw "Expected failure: $Code"
}

# No real native commands or SQL are used by this fixture.
$script:target = 'REL75_12345_1_1_abcdef012345'
$script:scenario = 'success'
$script:state = 'Stopped'
$script:calls = [Collections.Generic.List[string]]::new()
$script:readyCount = 0
function Invoke-LocalDbProbe {
    param([string]$Executable,[string[]]$Arguments,[int]$TimeoutMilliseconds,
        [switch]$KillEntireProcessTree,[Diagnostics.Stopwatch]$DeadlineWatch,[long]$DeadlineMilliseconds)
    if ($KillEntireProcessTree -or $TimeoutMilliseconds -le 0 -or $DeadlineMilliseconds -gt 60000) { throw 'Unsafe probe' }
    $verb = if ($Executable -ceq 'SQLCMD.EXE') { 'readiness' } else { $Arguments[0] }
    $script:calls.Add($verb)
    $out = ''; $err = ''; $exit = 0
    switch ($verb) {
        'versions' { $out = 'Microsoft SQL Server 2019 (15.0.4382.1)' }
        'create' {
            if ($Arguments[1] -cne $script:target) { throw 'Wrong mutation target' }
            if ($script:scenario -eq 'create-fail') { $out = 'SQL Server process failed to start' }
            else { $out = 'LocalDB instance "' + $script:target + '" created with version 15.0.' }
        }
        'info' {
            if ($Arguments.Count -eq 1) { $out = if ($script:scenario -eq 'collision') { $script:target } else { 'MSSQLLocalDB' } }
            elseif ($Arguments[1] -ceq 'MSSQLLocalDB') { $out = 'The automatic instance "MSSQLLocalDB" is not created.' }
            else {
                $name = if ($script:scenario -eq 'wrong-info') { 'DifferentInstance' } else { $script:target }
                $out = @("Name: $name","Version: 15.0.4382.1","State: $script:state",'Auto-create: No') -join [Environment]::NewLine
            }
        }
        'start' {
            if ($Arguments[1] -cne $script:target) { throw 'Wrong mutation target' }
            $script:state = 'Running'
            $out = 'LocalDB instance "' + $script:target + '" started.'
        }
        'readiness' {
            if ($Arguments[1] -cne "(localdb)\$script:target" -or $Arguments[3] -cne 'master') { throw 'Wrong read target' }
            $script:readyCount++
            if ($script:scenario -eq 'login-timeout' -and $script:readyCount -eq 1) {
                $err = 'Login timeout expired.'; $exit = 1
            } elseif ($script:scenario -eq 'mixed-error') {
                $err = 'Login timeout expired.' + [Environment]::NewLine + 'access denied'; $exit = 1
            } else { $out = '1' }
        }
        default { throw 'Unexpected command' }
    }
    return @{ stdout=$out; stderr=$err; exitCode=$exit; status=if ($exit) {'COMMAND_FAILED'} else {'OK'}; truncated=$false }
}

function Get-LocalDbToolVersion([string]$Name) {
    return @{ status='OK'; executable=$Name; version='2019.150.4382.1' }
}
function Reset-Rel75Fixture([string]$Scenario) {
    $script:scenario=$Scenario; $script:state='Stopped'; $script:calls.Clear(); $script:readyCount=0
}
function New-Rel75TestReport {
    return @{ coldVerified=$true; engineVersion=$null; readinessAttempts=0; tools=@{}; installedVersions=@(); selectedVersion=$null }
}

# Guard rejection occurs before any native probe.
Expect-Rel75Failure { Assert-Rel75Runner 'win22-auto' } 'HostedRunnerRequired'
Assert-Rel75Test ($script:calls.Count -eq 0) 'Guard performed native commands'
Expect-Rel75Failure { Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' (New-Rel75TestReport) } 'HostedRunnerRequired'
Assert-Rel75Test ($script:calls.Count -eq 0) 'Non-hosted alternative reached native boundary'
# Mock the hosted predicate only after checking actual guard; all native calls remain mocked.
function Test-LocalDbHostedWindows { return $true }

Reset-Rel75Fixture 'success'
$report=New-Rel75TestReport
$version=Get-Rel75Runtime (New-Rel75Context 15000) 'win22-named' $report
Assert-Rel75Test ($version -ceq '15.0.4382.1') 'Runtime binding failed'
$script:passed++

$missing=Join-Path ([IO.Path]::GetTempPath()) ('rel75-absent-' + [Guid]::NewGuid().ToString('N'))
Assert-Rel75Test ((Get-Rel75PathState $missing) -eq 'MISSING') 'Absent path not recognized'
Assert-Rel75Cold (New-Rel75Context 15000) 'MSSQLLocalDB' $missing
Assert-Rel75Cold (New-Rel75Context 15000) $script:target $missing
$script:passed++

Reset-Rel75Fixture 'collision'
Expect-Rel75Failure { Assert-Rel75Cold (New-Rel75Context 15000) $script:target $missing } 'NotCold'
Assert-Rel75Test ('create' -notin $script:calls) 'Collision caused mutation'

Reset-Rel75Fixture 'success'
Expect-Rel75Failure { Invoke-Rel75Alternative (New-Rel75Context 60000) 'foreign' '15.0.4382.1' (New-Rel75TestReport) } 'ForeignInstanceDenied'
Assert-Rel75Test ($script:calls.Count -eq 0) 'Foreign target reached native boundary'

Reset-Rel75Fixture 'success'
$report=New-Rel75TestReport
Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' $report
Assert-Rel75Test (($script:calls -join ',') -ceq 'create,info,start,info,readiness') 'Wrong cold path'
Assert-Rel75Test ($report.readinessAttempts -eq 1 -and $report.engineVersion -ceq '15.0.4382.1') 'No READY evidence'
$script:passed++

Reset-Rel75Fixture 'create-fail'
Expect-Rel75Failure { Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' (New-Rel75TestReport) } 'InstanceStartFailed'
Assert-Rel75Test (($script:calls -join ',') -ceq 'create') 'Create failure hidden by retry'

Reset-Rel75Fixture 'wrong-info'
Expect-Rel75Failure { Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' (New-Rel75TestReport) } 'InstanceMetadataMismatch'
Assert-Rel75Test ('start' -notin $script:calls) 'Wrong instance was started'

Reset-Rel75Fixture 'login-timeout'
$report=New-Rel75TestReport
Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' $report
Assert-Rel75Test ($report.readinessAttempts -eq 2 -and @($script:calls | Where-Object { $_ -eq 'create' }).Count -eq 1) 'Readiness retry policy changed'
$script:passed++

Reset-Rel75Fixture 'mixed-error'
Expect-Rel75Failure { Invoke-Rel75Alternative (New-Rel75Context 60000) $script:target '15.0.4382.1' (New-Rel75TestReport) } 'AccessDenied'
Assert-Rel75Test ($script:readyCount -eq 1) 'Fatal error retried'

Write-Host "REL-75.3 native boundary regressions: $script:passed passed."
