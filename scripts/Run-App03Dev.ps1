[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Resume', 'Remove', 'NextDay', 'Verify', 'VerifyWorkspace')]
    [string] $Action = 'Start',
    [ValidatePattern('^[a-f0-9]{32}$')]
    [string] $RunId,
    [string] $BusinessDate
)

$ErrorActionPreference = 'Stop'
$requiresRunId = $Action -in 'Stop', 'Resume', 'Remove', 'NextDay'
if ($requiresRunId -ne [bool] $RunId) {
    throw 'Supply -RunId for Stop, Resume, Remove or NextDay only.'
}
if (($Action -eq 'NextDay') -ne [bool] $BusinessDate) {
    throw 'Supply -BusinessDate (yyyy-MM-dd) for NextDay only.'
}
if ($Action -eq 'Stop') {
    if (!$IsWindows) { throw 'The APP-03 session requires Windows.' }
    if ($RunId -cnotmatch '\A[a-f0-9]{32}\z') { throw 'Invalid RunId.' }
    $app03Home = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FmcgDms/App03Dev'
    $folder = Join-Path $app03Home $RunId
    $manifestPath = Join-Path $folder 'session.json'
    $lockPath = Join-Path $app03Home "$RunId.lock"
    $signalName = "Local\FmcgDms.APP03.$RunId"

    function Assert-StopManifest {
        if (![IO.File]::Exists($manifestPath)) { throw 'APP-03 session manifest does not exist.' }
        if ([IO.File]::GetAttributes($folder).HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'APP-03 session directory cannot be a reparse point.'
        }
        $file = [IO.File]::Open($manifestPath, 'Open', 'Read', [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
        try {
            $reader = [IO.StreamReader]::new($file)
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
            finally { $reader.Dispose() }
        }
        finally { $file.Dispose() }
        if ($manifest.Format -cne 'APP-03-session-v1' -or $manifest.RunId -cne $RunId) {
            throw 'APP-03 session manifest does not match this run.'
        }
    }

    Assert-StopManifest
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ($wait.Elapsed.TotalSeconds -lt 30) {
        $sessionLock = $null
        try { $sessionLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { }
        if ($null -ne $sessionLock) {
            try { Assert-StopManifest }
            finally { $sessionLock.Dispose() }
            Write-Host "APP-03 DEV stopped $RunId; databases retained."
            return
        }

        $stopSignal = $null
        try { $stopSignal = [Threading.EventWaitHandle]::OpenExisting($signalName) }
        catch [Threading.WaitHandleCannotBeOpenedException] { }
        if ($null -ne $stopSignal) {
            try { [void] $stopSignal.Set() }
            finally { $stopSignal.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'APP-03 launcher did not stop within 30 seconds.'
}

$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $source = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $changes = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source state.' }
    if ($changes) { $source += '+working-tree' }

    $demo = 'src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll'
    if ($Action -ne 'Remove' -or !(Test-Path -LiteralPath $demo)) {
        dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'APP-03 DEV host build failed.' }
    }
    sqllocaldb start MSSQLLocalDB
    if ($LASTEXITCODE -ne 0) { throw 'MSSQLLocalDB did not start.' }
    $id = if ($RunId) { $RunId } else { '-' }
    $actionName = [char]::ToUpperInvariant($Action[0]) + $Action.Substring(1).ToLowerInvariant()
    $sessionArgs = @('App03Session', $actionName, $repo, $source, $id)
    if ($Action -eq 'NextDay') { $sessionArgs[1] = 'NextDay'; $sessionArgs += $BusinessDate }
    if ($Action -eq 'VerifyWorkspace') { $sessionArgs[1] = 'VerifyWorkspace' }
    dotnet $demo @sessionArgs
    if ($LASTEXITCODE -ne 0) { throw "APP-03 DEV $Action failed." }
}
finally { Pop-Location }
