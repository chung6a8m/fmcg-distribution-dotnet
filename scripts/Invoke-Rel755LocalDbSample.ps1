[CmdletBinding()]
param([Alias('Arm')][ValidateSet('win22-auto','win22-named','win25-auto')][string]$RequestedArm='win22-auto',
    [Alias('Iteration')][ValidateRange(1,20)][int]$RequestedIteration=1)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Invoke-Rel75LocalDbSample.ps1')
. (Join-Path $PSScriptRoot 'Rel755-ReadOnlyEvidence.ps1')
$script:rel755=[ordered]@{ schema='REL755_NATIVE_EVIDENCE_V1'; arm=$RequestedArm; iteration=$RequestedIteration
    sourceHeadSha=$env:REL75_SOURCE_SHA; workflowSha=$env:GITHUB_SHA; runId=$env:GITHUB_RUN_ID; runAttempt=$env:GITHUB_RUN_ATTEMPT
    instance=$null; guardFailure=$null; originalReceiptSha256=$null; sectorBefore=$null; sectorAfter=$null; sectorErrors=@()
    bootstrapStartUtc=$null; bootstrapEndUtc=$null
    monitor=@{status='NOT_STARTED'; cadenceMs=500; snapshots=@()}; startedUtc=[DateTime]::UtcNow.ToString('o'); completedUtc=$null }
$script:observerJob=$null
$script:observerControl=$null
function Stop-Rel755Observer {
    if ($null -eq $script:observerJob) { return }
    try {
        $marker=Join-Path $script:observerControl 'stop'
        if (![IO.File]::Exists($marker)) {
            $f=[IO.File]::Open($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); $f.Dispose()
        }
        $finished=Wait-Job -Job $script:observerJob -Timeout 8
        if ($null -ne $finished -and $finished.State -eq 'Completed') {
            $out=@(Receive-Job -Job $script:observerJob -ErrorAction Stop)
            if ($out.Count -eq 1) { $script:rel755.monitor=$out[0] | ConvertFrom-Json -AsHashtable -Depth 20 }
            else { $script:rel755.monitor.status='INVALID_OUTPUT' }
        } else { $script:rel755.monitor.status='MONITOR_INCOMPLETE' }
    } catch { $script:rel755.monitor.status='MONITOR_FAILED' }
    finally {
        try { if ($script:observerJob.State -notin 'Completed','Failed','Stopped') { Stop-Job -Job $script:observerJob } }
        finally { Remove-Job -Job $script:observerJob -Force; $script:observerJob=$null }
    }
}
$onCold={
    param($target,$directory)
    $script:rel755.instance=$target
    $script:rel755.sectorBefore=Get-Rel755Volume $directory
    $root=[IO.Path]::GetFullPath($env:RUNNER_TEMP)
    if ((Get-Rel75PathState $root) -ne 'PRESENT') { throw 'REL755_UNSAFE_TEMP' }
    $script:observerControl=Join-Path $root ('rel755-'+[Guid]::NewGuid().ToString('N'))
    if ((Get-Rel75PathState $script:observerControl) -ne 'MISSING') { throw 'REL755_CONTROL_EXISTS' }
    [IO.Directory]::CreateDirectory($script:observerControl) | Out-Null
    $monitorPath=Join-Path $PSScriptRoot 'Measure-Rel755Progress.ps1'
    $script:observerJob=Start-Job -ScriptBlock {
        param($scriptPath,$instance,$control)
        & $scriptPath -Target $instance -ControlDir $control
    } -ArgumentList $monitorPath,$target,$script:observerControl
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while ($wait.ElapsedMilliseconds -lt 10000 -and $script:observerJob.State -in 'NotStarted','Running') {
        if ([IO.File]::Exists((Join-Path $script:observerControl 'ready'))) {
            $script:rel755.bootstrapStartUtc=[DateTime]::UtcNow.ToString('o'); return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'REL755_MONITOR_NOT_READY'
}
$onFinished={
    param($target,$directory)
    if ($null -ne $script:rel755.bootstrapStartUtc) { $script:rel755.bootstrapEndUtc=[DateTime]::UtcNow.ToString('o') }
    Stop-Rel755Observer
    if ($script:rel755.instance -cne $target) { return }
    $script:rel755.sectorAfter=Get-Rel755Volume $directory
    $script:rel755.sectorErrors=@(Get-Rel755SectorErrors $directory)
}
$ok=$false
try {
    Assert-Rel75Runner $RequestedArm
    $ok=Invoke-Rel75Sample $RequestedArm $RequestedIteration -OnCold $onCold -OnFinished $onFinished
} catch {
    # A preflight rejection is not a cold observation. Retain a safe receipt
    # even when the legacy report is absent; never promote it in aggregation.
    $script:rel755.guardFailure=if ($_.Exception.Data.Contains('Rel75Code')) {
        $_.Exception.Data['Rel75Code']
    } else { 'UnexpectedWrapperFailure' }
    Write-Host "REL-75.5 wrapper stopped: $($script:rel755.guardFailure)"
} finally {
    Stop-Rel755Observer
    $original=Join-Path $PSScriptRoot '../TestResults/REL75-Matrix/sample.json'
    if ((Get-Rel75PathState $original) -eq 'PRESENT') { $script:rel755.originalReceiptSha256=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant() }
    $script:rel755.completedUtc=[DateTime]::UtcNow.ToString('o')
    $root=Join-Path $PSScriptRoot '../TestResults/REL75-Matrix'
    if ((Get-Rel75PathState $root) -notin 'PRESENT','MISSING') { throw 'REL755_UNSAFE_OUTPUT' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $path=Join-Path $root 'native.json'
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($script:rel755 | ConvertTo-Json -Depth 30))
    if ($bytes.Length -gt 524288) { throw 'REL755_EVIDENCE_LIMIT' }
    $file=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $file.Write($bytes,0,$bytes.Length) } finally { $file.Dispose() }
}
if (!$ok -or $script:rel755.monitor.status -ne 'COMPLETE' -or
    $null -eq $script:rel755.sectorBefore -or $script:rel755.sectorBefore.status -ne 'OK' -or
    $null -eq $script:rel755.sectorAfter -or $script:rel755.sectorAfter.status -ne 'OK') { exit 1 }
exit 0
