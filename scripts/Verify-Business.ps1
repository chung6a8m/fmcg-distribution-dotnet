[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$lock = Get-Content (Join-Path $root 'business.lock.json') -Raw | ConvertFrom-Json
$tree = (& git -C $root rev-parse HEAD:business 2>&1)
if ($LASTEXITCODE -ne 0 -or $tree.Trim() -ne $lock.businessTreeSha) {
    throw "Business Baseline tree mismatch: HEAD:business=$tree expected=$($lock.businessTreeSha)"
}
Write-Host "Business snapshot PASS: $tree"
