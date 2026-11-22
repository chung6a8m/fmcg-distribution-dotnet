[CmdletBinding()]
param([switch] $Verify)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $source = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
    $changes = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source state.' }
    if ($changes) { $source += '+working-tree' }
    dotnet build src/FmcgDms.Demo/FmcgDms.Demo.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'DEV host build failed.' }
    sqllocaldb start MSSQLLocalDB
    if ($LASTEXITCODE -ne 0) { throw 'MSSQLLocalDB did not start.' }
    $mode = if ($Verify) { 'Verify' } else { 'Interactive' }
    dotnet src/FmcgDms.Demo/bin/Release/net10.0/FmcgDms.Demo.dll App02C1 $mode $repo $source
    if ($LASTEXITCODE -ne 0) { throw 'APP-02C.1 DEV session failed.' }
}
finally { Pop-Location }
