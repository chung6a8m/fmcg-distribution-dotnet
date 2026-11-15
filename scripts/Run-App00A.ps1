[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
& "$PSScriptRoot/Verify-Business.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet restore FmcgDms.slnx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build FmcgDms.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet test FmcgDms.slnx --configuration Release --no-build --logger trx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location web
try {
    & npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & npm run build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
Write-Host 'APP-00A local verification completed.'
