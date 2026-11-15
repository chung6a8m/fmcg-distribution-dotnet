[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# HEAD's tree alone ignores staged, modified and untracked business files on disk.
$changes = @(& git -C $root status --porcelain=v1 --untracked-files=all -- business/ 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Unable to inspect business/ working tree: $changes" }
if ($changes.Count -gt 0) {
    throw "Business Baseline working tree must be clean: $($changes -join '; ')"
}
$lock = Get-Content (Join-Path $root 'business.lock.json') -Raw | ConvertFrom-Json
$tree = (& git -C $root rev-parse HEAD:business 2>&1)
if ($LASTEXITCODE -ne 0 -or $tree.Trim() -ne $lock.businessTreeSha) {
    throw "Business Baseline tree mismatch: HEAD:business=$tree expected=$($lock.businessTreeSha)"
}
Write-Host "Business snapshot PASS: $tree"
