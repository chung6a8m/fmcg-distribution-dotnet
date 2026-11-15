[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
& "$PSScriptRoot/Verify-Business.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& python tools/validate_business.py
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet restore FmcgDms.slnx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build FmcgDms.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet test --solution FmcgDms.slnx --configuration Release --no-build --minimum-expected-tests 9 -- --report-trx --coverage --coverage-output-format cobertura
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location web
try {
    & npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & npm run test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & npm run build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
Write-Host 'APP-00A local verification completed.'
