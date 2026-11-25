[CmdletBinding()]
param([switch] $Verify, [ValidateSet('App02C1', 'App02C2', 'App02C3', 'App02C4', 'App02C5', 'App02C6', 'App02D1', 'App02D2', 'App02D3', 'App02D4', 'DevF01', 'DevF02', 'DevF03', 'DevF04', 'DevF05', 'DevF06', 'DevF07', 'DevF08', 'App03A1')] [string] $Increment = 'App02C1')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $source = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $changes = git status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source state.' }
    if ($changes) { $source += '+working-tree' }
    . (Join-Path $PSScriptRoot 'Use-CiPrebuiltDemo.ps1')
    if (!(Test-CiPrebuiltDemo -RepositoryRoot $repo -SourceCommit $source)) {
        dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'DEV host build failed.' }
    }
    sqllocaldb start MSSQLLocalDB
    if ($LASTEXITCODE -ne 0) { throw 'MSSQLLocalDB did not start.' }
    $mode = if ($Verify) { 'Verify' } else { 'Interactive' }
    dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll $Increment $mode $repo $source
    if ($LASTEXITCODE -ne 0) { throw "$Increment DEV session failed." }
}
finally { Pop-Location }
