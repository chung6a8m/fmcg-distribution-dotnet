[CmdletBinding()]
param(
    [ValidateSet('win22-auto','win22-named','win25-auto')][string]$Arm = 'win22-auto',
    [ValidateRange(1,20)][int]$Iteration = 1
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-LocalDb.ps1')

# Investigation only; the required-CI bootstrap and its target guard are unchanged.
function Stop-Rel75([string]$Code) {
    $ex = [InvalidOperationException]::new('REL-75.3 observation stopped.')
    $ex.Data['Rel75Code'] = $Code
    throw $ex
}

function Get-Rel75PathState([string]$Path) {
    try {
        $current = [IO.Path]::GetFullPath($Path)
        $missing = $false
        while ($current) {
            try {
                if ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) {
                    return 'REPARSE_POINT'
                }
            } catch [IO.FileNotFoundException] { $missing = $true }
              catch [IO.DirectoryNotFoundException] { $missing = $true }
            $parent = [IO.Directory]::GetParent($current)
            $current = if ($null -ne $parent) { $parent.FullName } else { $null }
        }
        if ($missing) { return 'MISSING' }
        return 'PRESENT'
    } catch [UnauthorizedAccessException] { return 'ACCESS_DENIED' }
      catch { return 'UNAVAILABLE' }
}

function Get-Rel75Files([string]$Directory) {
    # Fixed basenames and metadata only; no MDF/LDF reads, hashes or private paths.
    return @(foreach ($name in 'master.mdf','mastlog.ldf','model.mdf','modellog.ldf',
        'msdbdata.mdf','msdblog.ldf','model_msdbdata.mdf','model_msdblog.ldf',
        'model_replicatedmaster.mdf','model_replicatedmaster.ldf','error.log','error1.log','error2.log') {
        $path = Join-Path $Directory $name
        $row = [ordered]@{ file = $name; status = Get-Rel75PathState $path }
        if ($row.status -eq 'PRESENT') {
            try {
                $file = [IO.FileInfo]::new($path)
                if (($file.Attributes -band [IO.FileAttributes]::Directory) -or
                    ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    $row.status = 'UNEXPECTED_FILE_TYPE'
                } else {
                    $row.sizeBytes = $file.Length
                    $row.createdUtc = $file.CreationTimeUtc.ToString('o')
                    $row.modifiedUtc = $file.LastWriteTimeUtc.ToString('o')
                }
            } catch [UnauthorizedAccessException] { $row.status = 'ACCESS_DENIED' }
              catch { $row.status = 'UNAVAILABLE' }
        }
        [pscustomobject]$row
    })
}

function Get-Rel75Disk([string]$Directory) {
    try {
        $disk = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Directory))
        $format = $disk.DriveFormat
        return @{ status = 'OK'; format = if ($format -in 'NTFS','ReFS','FAT32','exFAT') { $format } else { 'OTHER' }
            freeBytes = $disk.AvailableFreeSpace; totalBytes = $disk.TotalSize }
    } catch { return @{ status = 'UNAVAILABLE' } }
}

function New-Rel75Name([string]$SelectedArm, [int]$Sample) {
    if ($SelectedArm -ne 'win22-named') { return 'MSSQLLocalDB' }
    return ('REL75_{0}_{1}_{2}_{3}' -f $env:GITHUB_RUN_ID,$env:GITHUB_RUN_ATTEMPT,$Sample,[Guid]::NewGuid().ToString('N').Substring(0,12))
}

function Assert-Rel75Runner([string]$SelectedArm) {
    if (!(Test-LocalDbHostedWindows) -or $env:GITHUB_EVENT_NAME -cnotin 'pull_request','workflow_dispatch' -or
        $env:GITHUB_RUN_ID -cnotmatch '\A[0-9]{1,20}\z' -or
        $env:GITHUB_RUN_ATTEMPT -cnotmatch '\A[0-9]{1,10}\z' -or
        $env:REL75_SOURCE_SHA -cnotmatch '\A[a-f0-9]{40}\z' -or
        $env:GITHUB_SHA -cnotmatch '\A[a-f0-9]{40}\z' -or
        $env:GITHUB_JOB -cne 'sample') { Stop-Rel75 'HostedRunnerRequired' }
    if ($env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch' -and $env:GITHUB_REF -cne 'refs/heads/main') {
        Stop-Rel75 'MainDispatchRequired'
    }
    $expected = if ($SelectedArm -eq 'win25-auto') { 26100 } else { 20348 }
    if ([Environment]::OSVersion.Version.Build -ne $expected) { Stop-Rel75 'OsBuildMismatch' }
    $actual = git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or "$actual".Trim() -cne $env:REL75_SOURCE_SHA) { Stop-Rel75 'SourceMismatch' }
}

function New-Rel75Context([long]$Limit) {
    return @{ watch = [Diagnostics.Stopwatch]::StartNew(); limit = $Limit; history = [Collections.Generic.List[object]]::new() }
}

function Invoke-Rel75Stage([hashtable]$Context, [string]$Stage, [string[]]$Arguments, [int]$Attempt = 1) {
    $remaining = $Context.limit - $Context.watch.ElapsedMilliseconds
    if ($remaining -le 0) { Stop-Rel75 'DeadlineExceeded' }
    $cap = switch ($Stage) { 'create' { 45000 } 'start' { 15000 } 'readiness' { 12000 } default { 3000 } }
    $timeout = [int][Math]::Min($cap,$remaining)
    $exe = if ($Stage -eq 'readiness') { 'SQLCMD.EXE' } else { 'SqlLocalDB.exe' }
    $params = @{ Executable = $exe; Arguments = $Arguments; TimeoutMilliseconds = $timeout
        KillEntireProcessTree = $false; DeadlineWatch = $Context.watch; DeadlineMilliseconds = $Context.limit }
    $native = Invoke-LocalDbProbe @params
    $native.stdout = $native.stdout -replace '\r+\n', [string][char]10
    $native.stderr = $native.stderr -replace '\r+\n', [string][char]10
    $message = $native.stdout + [Environment]::NewLine + $native.stderr
    $kind = Get-LocalDbBootstrapError $message
    if ($native.truncated) { $kind = 'OutputTruncated' }
    elseif ($native.status -eq 'TIMEOUT') { $kind = 'NativeProcessTimeout' }
    elseif ($native.status -eq 'ACCESS_DENIED') { $kind = 'AccessDenied' }
    elseif ($native.status -eq 'COMMAND_NOT_FOUND') { $kind = 'ToolUnavailable' }
    elseif ($native.status -notin 'OK','COMMAND_FAILED') { $kind = 'NativeProbeFailed' }
    elseif ($kind -eq 'None' -and ($native.status -ne 'OK' -or $native.exitCode -ne 0 -or $null -eq $native.exitCode)) {
        $kind = 'NativeCommandFailed'
    }
    $code = [regex]::Match($message, '(?i)\b0x[0-9a-f]{8}\b').Value
    $row = [ordered]@{ stage = $Stage; attempt = $Attempt; status = $native.status; exitCode = $native.exitCode
        error = $kind; nativeDetail = if ($kind -ne 'None') { Get-LocalDbNativeDetail $message } else { $null }
        nativeCode = if ($code) { $code } else { $null }
        truncated = [bool]$native.truncated; timeoutMilliseconds = $timeout; elapsedMilliseconds = $Context.watch.ElapsedMilliseconds }
    $Context.history.Add([pscustomobject]$row)
    if ($Context.watch.ElapsedMilliseconds -ge $Context.limit) { Stop-Rel75 'DeadlineExceeded' }
    return @{ record = $row; native = $native }
}

function Assert-Rel75Ok([hashtable]$Stage) {
    if ($Stage.record.error -ne 'None') { Stop-Rel75 $Stage.record.error }
    if ($Stage.native.stderr.Trim()) { Stop-Rel75 'UnexpectedNativeOutput' }
}

function Get-Rel75Runtime([hashtable]$Context, [string]$SelectedArm, [System.Collections.IDictionary]$Report) {
    $Report.tools = @{ localDb = Get-LocalDbToolVersion 'SqlLocalDB.exe'; sqlcmd = Get-LocalDbToolVersion 'SQLCMD.EXE' }
    if ($Report.tools.localDb.status -ne 'OK' -or $Report.tools.sqlcmd.status -ne 'OK') { Stop-Rel75 'ToolUnavailable' }
    $stage = Invoke-Rel75Stage $Context 'versions' @('versions')
    Assert-Rel75Ok $stage
    $versions = @(foreach ($line in ($stage.native.stdout -split '\r?\n')) {
        if (!$line.Trim()) { continue }
        $m = [regex]::Match($line.Trim(), '\AMicrosoft SQL Server (?:2019|2022|2025) \(((?:15|16|17)\.0\.\d+\.\d+)\)\z')
        if (!$m.Success) { Stop-Rel75 'UnsupportedRuntimeInventory' }
        $m.Groups[1].Value
    })
    $Report.installedVersions = $versions
    if ($versions.Count -ne 1) { Stop-Rel75 'UnsupportedRuntimeInventory' }
    $version = $versions[0]
    $normalized = $Report.tools.localDb.version -replace '^2019\.150\.','15.0.' -replace '^2022\.160\.','16.0.' -replace '^2025\.170\.','17.0.'
    if ($normalized -cne $version -or ($SelectedArm -ne 'win25-auto' -and $version -cnotmatch '\A15\.0\.')) {
        Stop-Rel75 'VersionMismatch'
    }
    $Report.selectedVersion = $version
    return $version
}

function Get-Rel75Instance([hashtable]$Context, [string]$Target, [string]$Version, [int]$Attempt = 1) {
    $info = Invoke-Rel75Stage $Context 'info' @('info',$Target) $Attempt
    Assert-Rel75Ok $info
    $fields = @{}
    foreach ($key in 'Name','Version','State','Auto-create') {
        $m = [regex]::Matches($info.native.stdout, '(?im)^' + [regex]::Escape($key) + ':[ \t]*([^\r\n]*)$')
        if ($m.Count -ne 1) { Stop-Rel75 'InstanceMetadataMismatch' }
        $fields[$key] = $m[0].Groups[1].Value.Trim()
    }
    $automatic = if ($Target -ceq 'MSSQLLocalDB') { 'Yes' } else { 'No' }
    if ($fields.Name -cne $Target -or $fields.Version -cne $Version -or
        $fields.State -cnotin 'Stopped','Running' -or $fields['Auto-create'] -ine $automatic) {
        Stop-Rel75 'InstanceMetadataMismatch'
    }
    return $fields.State
}

function Assert-Rel75Cold([hashtable]$Context, [string]$Target, [string]$Directory) {
    $state = Get-Rel75PathState $Directory
    if ($state -eq 'PRESENT') { Stop-Rel75 'NotCold' }
    if ($state -ne 'MISSING') { Stop-Rel75 'UnsafeInstanceDirectory' }
    if ($Target -ceq 'MSSQLLocalDB') {
        $info = Invoke-Rel75Stage $Context 'info' @('info',$Target)
        if ($info.native.truncated -or $info.native.status -notin 'OK','COMMAND_FAILED' -or
            $info.record.error -ne 'AutomaticInstanceNotCreated') {
            if ($info.record.error -eq 'None') { Stop-Rel75 'NotCold' }
            Stop-Rel75 'ColdStateUnverified'
        }
    } else {
        if ($Target -cnotmatch '\AREL75_[0-9]{1,20}_[0-9]{1,10}_[0-9]{1,2}_[a-f0-9]{12}\z') { Stop-Rel75 'ForeignInstanceDenied' }
        $list = Invoke-Rel75Stage $Context 'list' @('info')
        Assert-Rel75Ok $list
        foreach ($line in ($list.native.stdout -split '\r?\n')) {
            if ($line.Trim() -ieq $Target) { Stop-Rel75 'NotCold' }
        }
    }
}

function Invoke-Rel75Alternative([hashtable]$Context, [string]$Target, [string]$Version, [System.Collections.IDictionary]$Report) {
    # Only this experiment may use its generated named target or a verified cold automatic instance.
    if (!(Test-LocalDbHostedWindows)) { Stop-Rel75 'HostedRunnerRequired' }
    if (!$Report.coldVerified -or $Version -cnotmatch '\A(?:15|16|17)\.0\.\d+\.\d+\z' -or
        ($Target -cne 'MSSQLLocalDB' -and $Target -cnotmatch '\AREL75_[0-9]{1,20}_[0-9]{1,10}_[0-9]{1,2}_[a-f0-9]{12}\z')) {
        Stop-Rel75 'ForeignInstanceDenied'
    }
    $family = ($Version -split '\.')[0] + '.0'
    $create = Invoke-Rel75Stage $Context 'create' @('create',$Target)
    Assert-Rel75Ok $create
    if ($create.native.stdout.Trim() -cnotmatch ('\ALocalDB instance "' + [regex]::Escape($Target) +
        '" created with version ' + [regex]::Escape($family) + '(?:\.\d+){0,2}\.\z')) { Stop-Rel75 'UnexpectedNativeOutput' }
    $state = Get-Rel75Instance $Context $Target $Version
    if ($state -eq 'Stopped') {
        $start = Invoke-Rel75Stage $Context 'start' @('start',$Target)
        Assert-Rel75Ok $start
        if ($start.native.stdout.Trim() -cne ('LocalDB instance "' + $Target + '" started.')) { Stop-Rel75 'UnexpectedNativeOutput' }
        $state = Get-Rel75Instance $Context $Target $Version
    }
    if ($state -ne 'Running') { Stop-Rel75 'InstanceNotRunning' }
    $Report.engineVersion = $Version
    $query = "SET NOCOUNT ON; SELECT CASE WHEN CONVERT(int,SERVERPROPERTY('IsLocalDB'))=1 AND CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'))='$Version' AND DB_NAME()='master' THEN 1 ELSE 0 END;"
    for ($i=1; $i -le 3; $i++) {
        $Report.readinessAttempts = $i
        $ready = Invoke-Rel75Stage $Context 'readiness' @('-S',"(localdb)\$Target",'-d','master','-E','-b','-l','5','-t','5','-h','-1','-W','-Q',$query) $i
        if ($ready.record.error -eq 'None') {
            Assert-Rel75Ok $ready
            if ($ready.native.stdout.Trim() -cne '1') { Stop-Rel75 'ReadinessResponseMismatch' }
            return
        }
        if ($ready.record.error -ne 'LoginTimeout') { Stop-Rel75 $ready.record.error }
        if ($i -eq 3) { Stop-Rel75 'ReadinessAttemptsExhausted' }
        if ((Get-Rel75Instance $Context $Target $Version $i) -ne 'Running') { Stop-Rel75 'InstanceNotRunning' }
        if (($Context.limit - $Context.watch.ElapsedMilliseconds) -le 2000) { Stop-Rel75 'DeadlineExceeded' }
        Start-Sleep -Milliseconds 2000
        if ((Get-Rel75Instance $Context $Target $Version $i) -ne 'Running') { Stop-Rel75 'InstanceNotRunning' }
    }
}

function Write-Rel75Receipt([System.Collections.IDictionary]$Report) {
    $root = Join-Path $PSScriptRoot '../TestResults/REL75-Matrix'
    if ((Get-Rel75PathState $root) -notin 'MISSING','PRESENT') { throw 'Unsafe evidence directory.' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $path = Join-Path $root 'sample.json'
    if ((Get-Rel75PathState $path) -ne 'MISSING') { throw 'Evidence already exists.' }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Report | ConvertTo-Json -Depth 20))
    $tmp = Join-Path $root ('sample-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = [IO.File]::Open($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    try { [IO.File]::Move($tmp,$path) } finally { [IO.File]::Delete($tmp) }
}

function Invoke-Rel75Sample([string]$SelectedArm, [int]$Sample) {
    Assert-Rel75Runner $SelectedArm
    $target = New-Rel75Name $SelectedArm $Sample
    $profilePath = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrEmpty($profilePath)) { Stop-Rel75 'ProfileUnavailable' }
    $dir = Join-Path $profilePath "Microsoft\Microsoft SQL Server Local DB\Instances\$target"
    $report = [ordered]@{
        schema = 'REL75_LOCALDB_SAMPLE_V1'; arm = $SelectedArm; iteration = $Sample
        sourceHeadSha = $env:REL75_SOURCE_SHA; workflowSha = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        osBuild = [Environment]::OSVersion.Version.Build
        imageVersion = if ($env:ImageVersion -cmatch '\A[0-9.]{1,32}\z') { $env:ImageVersion } else { $null }
        instance = $target; startedUtc = [DateTime]::UtcNow.ToString('o'); completedUtc = $null
        status = 'NOT_EXECUTED'; failure = $null; coldVerified = $false
        bootstrapKind = $null; elapsedMilliseconds = 0; readinessAttempts = 0
        selectedVersion = $null; engineVersion = $null; installedVersions = @(); tools = @{}
        preflight = @(); bootstrap = @(); controlReceipt = $null
        diskBefore = Get-Rel75Disk $dir; filesBefore = @(Get-Rel75Files $dir)
        diskAfter = $null; filesAfter = @(); logs = @(); applicationEvents = $null
    }
    $pre = New-Rel75Context 15000
    $boot = @{ watch = [Diagnostics.Stopwatch]::new(); limit = 60000; history = [Collections.Generic.List[object]]::new() }
    try {
        $version = Get-Rel75Runtime $pre $SelectedArm $report
        Assert-Rel75Cold $pre $target $dir
        $report.coldVerified = $true
        $report.status = 'BOOTSTRAP_FAILED'
        $boot.watch.Start()
        if ($target -ceq 'MSSQLLocalDB' -and $version -cmatch '\A15\.0\.') {
            $report.bootstrapKind = 'EXISTING_15_CONTROL'
            $env:CI_LOCALDB_SOURCE_HEAD_SHA = $env:REL75_SOURCE_SHA
            $control = Initialize-LocalDb
            $report.controlReceipt = $control
            $report.engineVersion = $control.engineVersion
            $report.readinessAttempts = $control.readinessAttempts
            $report.bootstrap = @($control.attempts)
            $report.elapsedMilliseconds = $control.elapsedMilliseconds
            if ($control.mode -ne 'COLD') { Stop-Rel75 'ControlNotCold' }
            if ($control.status -ne 'READY') { Stop-Rel75 $control.failure }
            if ($control.engineVersion -cne $version) { Stop-Rel75 'VersionMismatch' }
        } else {
            $report.bootstrapKind = 'EXPERIMENT_DEFAULT_TOOL'
            Invoke-Rel75Alternative $boot $target $version $report
        }
        $report.status = 'READY'
    } catch {
        $report.failure = if ($_.Exception.Data.Contains('Rel75Code')) { $_.Exception.Data['Rel75Code'] } else { 'UnexpectedSampleFailure' }
        if ($report.failure -in 'NotCold','ControlNotCold') { $report.status = 'NOT_COLD' }
    } finally {
        $pre.watch.Stop(); $boot.watch.Stop()
        $report.preflight = @($pre.history.ToArray())
        if ($null -eq $report.controlReceipt) {
            $report.bootstrap = @($boot.history.ToArray())
            $report.elapsedMilliseconds = $boot.watch.ElapsedMilliseconds
        }
        if ($report.status -eq 'BOOTSTRAP_FAILED' -and @($report.bootstrap | Where-Object stage -eq 'create').Count -eq 0) {
            $report.status = 'NOT_EXECUTED'
        }
        # Collection failure must not replace the actual bootstrap outcome.
        try { $report.diskAfter = Get-Rel75Disk $dir; $report.filesAfter = @(Get-Rel75Files $dir) } catch { }
        try {
            $report.logs = @(foreach ($name in 'error.log','error1.log','error2.log') {
                $path = Join-Path $dir $name
                $meta = Get-Rel75PathState $path
                if ($meta -eq 'PRESENT') { @{ file = $name; observation = Get-LocalDbLogErrorCodes $path } }
                else { @{ file = $name; observation = @{ status = $meta; bytesRead = 0; truncated = $false; sqlErrors = @() } } }
            })
        } catch { $report.logs = @() }
        try { $report.applicationEvents = Get-LocalDbEventMetadata } catch { $report.applicationEvents = @{ status = 'PROBE_FAILED'; events = @() } }
        $report.completedUtc = [DateTime]::UtcNow.ToString('o')
        # Each job owns a disposable hosted VM. No stop/delete/reset.
        Write-Rel75Receipt $report
    }
    Write-Host "REL-75.3 $SelectedArm/$Sample $($report.status) ($($report.failure)); elapsedMs=$($report.elapsedMilliseconds)"
    return $report.status -eq 'READY'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if (Invoke-Rel75Sample $Arm $Iteration) { exit 0 }
        exit 1
    } catch {
        Write-Host "REL-75.3 sample could not complete ($($_.Exception.GetBaseException().GetType().Name))."
        exit 1
    }
}
