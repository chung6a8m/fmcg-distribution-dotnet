[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Resume', 'Remove', 'Verify')]
    [string] $Action = 'Start',
    [ValidatePattern('^[a-f0-9]{32}$')]
    [string] $RunId
)

$ErrorActionPreference = 'Stop'
$requiresRunId = $Action -in 'Stop', 'Resume', 'Remove'
if ($requiresRunId -ne [bool] $RunId) {
    throw 'Supply -RunId for Stop, Resume or Remove only.'
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
    if ($Action -notin 'Stop', 'Remove' -or !(Test-Path -LiteralPath $demo)) {
        dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'APP-03 DEV host build failed.' }
    }
    if ($Action -ne 'Stop') {
        sqllocaldb start MSSQLLocalDB
        if ($LASTEXITCODE -ne 0) { throw 'MSSQLLocalDB did not start.' }
    }
    $id = if ($RunId) { $RunId } else { '-' }
    $actionName = [char]::ToUpperInvariant($Action[0]) + $Action.Substring(1).ToLowerInvariant()
    dotnet $demo App03Session $actionName $repo $source $id
    if ($LASTEXITCODE -ne 0) { throw "APP-03 DEV $Action failed." }
}
finally { Pop-Location }
