[CmdletBinding()]
param([Parameter(Mandatory)][string]$Target,[Parameter(Mandatory)][string]$ControlDir)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Invoke-Rel75LocalDbSample.ps1')
if (!(Test-LocalDbHostedWindows) -or $env:GITHUB_JOB -cne 'sample' -or
    $env:GITHUB_EVENT_NAME -cnotin 'pull_request','workflow_dispatch' -or
    $env:GITHUB_RUN_ID -cnotmatch '\A[0-9]{1,20}\z' -or $env:GITHUB_RUN_ATTEMPT -cnotmatch '\A[0-9]{1,10}\z' -or
    ($Target -cne 'MSSQLLocalDB' -and $Target -cnotmatch ('\AREL75_' + $env:GITHUB_RUN_ID + '_' + $env:GITHUB_RUN_ATTEMPT + '_[0-9]{1,2}_[a-f0-9]{12}\z'))) {
    throw 'REL755_MONITOR_BOUNDARY'
}
$root=[IO.Path]::GetFullPath($env:RUNNER_TEMP)
$control=[IO.DirectoryInfo]::new([IO.Path]::GetFullPath($ControlDir))
if ($control.Parent.FullName -ine $root -or $control.Name -cnotmatch '\Arel755-[a-f0-9]{32}\z' -or
    (Get-Rel75PathState $control.FullName) -ne 'PRESENT') { throw 'REL755_MONITOR_CONTROL' }
$dir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Microsoft\Microsoft SQL Server Local DB\Instances\$Target"
if ((Get-Rel75PathState $dir) -ne 'MISSING') { throw 'REL755_MONITOR_NOT_COLD' }
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$watch=[Diagnostics.Stopwatch]::StartNew()
$rows=[Collections.Generic.List[object]]::new()
$status='LIMIT_REACHED'
try {
    do {
        $processes=@(foreach ($p in @(Get-Process -Name SqlLocalDB,sqlservr -ErrorAction SilentlyContinue)) {
            try {
                if ($p.SessionId -ne $session) { continue }
                @{ kind=if ($p.ProcessName -ieq 'sqlservr') {'ENGINE'} else {'LAUNCHER'}; pid=$p.Id
                    startedUtc=$p.StartTime.ToUniversalTime().ToString('o'); cpuMs=[long]$p.TotalProcessorTime.TotalMilliseconds }
            } catch { } finally { $p.Dispose() }
        })
        $logPath=Join-Path $dir 'error.log'
        $log=[ordered]@{ status=Get-Rel75PathState $logPath; sizeBytes=$null; modifiedUtc=$null }
        if ($log.status -eq 'PRESENT') {
            try {
                $f=[IO.FileInfo]::new($logPath)
                if (($f.Attributes -band [IO.FileAttributes]::Directory) -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    $log.status='UNEXPECTED_FILE_TYPE'
                } else { $log.sizeBytes=$f.Length; $log.modifiedUtc=$f.LastWriteTimeUtc.ToString('o') }
            } catch { $log.status='UNAVAILABLE' }
        }
        $rows.Add(@{ utc=[DateTime]::UtcNow.ToString('o'); elapsedMs=$watch.ElapsedMilliseconds; processes=$processes; log=$log })
        if ($rows.Count -eq 1) {
            $ready=[IO.File]::Open((Join-Path $control.FullName 'ready'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            $ready.Dispose()
        }
        if ([IO.File]::Exists((Join-Path $control.FullName 'stop'))) { $status='COMPLETE'; break }
        Start-Sleep -Milliseconds 500
    } while ($watch.ElapsedMilliseconds -lt 85000 -and $rows.Count -lt 170)
} finally {
    # One JSON projection only; process paths, arguments and raw log contents are never retained.
    @{ status=$status; cadenceMs=500; snapshots=@($rows.ToArray()) } | ConvertTo-Json -Depth 12 -Compress
}
