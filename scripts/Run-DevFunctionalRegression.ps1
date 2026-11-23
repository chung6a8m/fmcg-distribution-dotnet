[CmdletBinding()]
param(
    [ValidateSet('All', 'Functional', 'Foundation')]
    [string] $Suite = 'All',
    [switch] $ManifestOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo

$independentSources = [ordered]@{
    'E-SIM-01' = 'b82a68d72940c8834341f50b484e063c386f8bb7'
    'DEV-F02'  = '72dc78e51f63ad940e766e9d8912eb35c465f908'
}
$functional = @(
    @{ Name = 'DEV-F01'; Profile = 'DevF01'; Folder = 'dev-f01'; Cases = 'F01-01/F01-03 browser; F01-02 .NET'; AC = 'AC-17' }
    @{ Name = 'DEV-F02'; Profile = 'DevF02'; Folder = 'dev-f02'; Cases = 'F02-01 through F02-04'; AC = 'AC-20' }
    @{ Name = 'DEV-F03'; Profile = 'DevF03'; Folder = 'dev-f03'; Cases = 'F03-01 through F03-04 within fixture'; AC = 'AC-05' }
    @{ Name = 'DEV-F04'; Profile = 'DevF04'; Folder = 'dev-f04'; Cases = 'F04-01 through F04-04 within fixture'; AC = 'AC-19 pricing' }
    @{ Name = 'DEV-F05'; Profile = 'DevF05'; Folder = 'dev-f05'; Cases = 'F05-01 through F05-04'; AC = 'AC-05/06' }
    @{ Name = 'DEV-F06'; Profile = 'DevF06'; Folder = 'dev-f06'; Cases = 'F06-01 through F06-04'; AC = 'AC-06' }
    @{ Name = 'DEV-F07'; Profile = 'DevF07'; Folder = 'dev-f07'; Cases = 'F07-01 through F07-04'; AC = 'AC-07' }
    @{ Name = 'DEV-F08'; Profile = 'DevF08'; Folder = 'dev-f08'; Cases = 'F08-01 through F08-04'; AC = 'AC-19 returns' }
)
$foundation = @(
    @{ Name = 'C.6'; Profile = 'App02C6'; Folder = 'app02c6'; File = 'sql-review.json'; Count = 15 }
    @{ Name = 'D.1'; Profile = 'App02D1'; Folder = 'app02d1'; File = 'sql-review.json'; Count = 28 }
    @{ Name = 'D.2'; Profile = 'App02D2'; Folder = 'app02d2'; File = 'sql-review.json'; Count = 36 }
    @{ Name = 'D.3'; Profile = 'App02D3'; Folder = 'app02d3'; File = 'sql-review.json'; Count = 36 }
    @{ Name = 'D.4'; Profile = 'App02D4'; Folder = 'app02d4'; File = 'sql-matrix.json'; Count = 36 }
)
$envNames = @('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_SHA', 'APP02A4_TEST_RUN_ID', 'APP02A4_SOURCE_HEAD_SHA')
$previousEnvironment = @{}

function Get-Receipt([string] $Path) {
    $file = Get-Item -LiteralPath $Path
    return @{
        path = [IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}

function Invoke-Run([string] $Name, [string] $Program, [string[]] $ArgumentList) {
    $log = Join-Path $output "$Name.log"
    $began = [DateTimeOffset]::UtcNow
    $failure = $null
    $code = 1
    try {
        & $Program @ArgumentList *> $log
        $code = $LASTEXITCODE
        if ($code -ne 0) { $failure = "Exit $code, see $Name.log" }
    } catch {
        $failure = $_.Exception.Message
        Add-Content -LiteralPath $log -Value $failure
    }
    $run = [ordered]@{
        suite = $Name
        command = @($Program) + $ArgumentList
        startedUtc = $began.ToString('o')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' })
        error = $failure
        runId = $null
        log = Get-Receipt $log
        receipts = @()
    }
    $runs.Add($run)
    return $run
}

function Get-LocalDbCatalog {
    $catalog = @(sqlcmd -S '(localdb)\MSSQLLocalDB' -E -C -b -h -1 -W -Q 'SET NOCOUNT ON; SELECT [name] FROM sys.databases ORDER BY [name];')
    if ($LASTEXITCODE -ne 0 -or $catalog.Count -lt 4) { throw 'Unable to read the LocalDB catalog' }
    return $catalog
}

function Receive-Scenario($Entry, [bool] $Baseline) {
    $root = Join-Path 'TestResults' $Entry.Folder
    $filename = if ($Baseline) { $Entry.File } else { 'sql-result.json' }
    $old = @()
    if (Test-Path -LiteralPath $root) {
        $old = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter $filename | ForEach-Object FullName)
    }
    $run = Invoke-Run $Entry.Name 'pwsh' @('-NoProfile', '-File', './scripts/Run-App02C1.ps1', '-Increment', $Entry.Profile, '-Verify')
    if (!$Baseline) {
        $run.ac = $Entry.AC
        $run.cases = $Entry.Cases
    }
    if ($run.status -ne 'PASS') {
        Write-Host "$($Entry.Name) FAIL — $($run.error)"
        return
    }
    try {
        $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter $filename |
            Where-Object FullName -notin $old)
        if ($files.Count -ne 1) { throw "Expected one new SQL result, observed $($files.Count)" }
        $sql = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json -AsHashtable
        if ($sql.source -cne $source -or $sql.runId -cne $files[0].Directory.Name) {
            throw 'SQL report source or run ID does not match'
        }
        $run.runId = $sql.runId
        $run.receipts += Get-Receipt $files[0].FullName
        if ($Baseline) {
            $run.expected = $Entry.Count
            $run.actual = @($sql.cases | Where-Object { $_.status -ceq 'PASS' }).Count
            if ($run.actual -ne $run.expected -or @($sql.cases).Count -ne $run.expected) {
                throw 'Baseline case count or status has changed'
            }
        }
        $junit = @(Get-ChildItem -LiteralPath web -Directory -Filter 'test-results*' |
            ForEach-Object { Join-Path $_.FullName "$($sql.runId)/junit.xml" } |
            Where-Object { Test-Path -LiteralPath $_ })
        if ($junit.Count -ne 1) { throw "Expected one browser JUnit result, observed $($junit.Count)" }
        [xml] $browser = Get-Content -LiteralPath $junit[0] -Raw
        $totals = $browser.testsuites
        $run.browserPassed = [int] $totals.tests - [int] $totals.failures - [int] $totals.errors - [int] $totals.skipped
        if ($run.browserPassed -le 0 -or [int] $totals.tests -ne $run.browserPassed) {
            throw 'Browser result has a failed, skipped or missing case'
        }
        $run.receipts += Get-Receipt $junit[0]
    } catch {
        $run.status = 'FAIL'
        $run.error = $_.Exception.Message
    }
    Write-Host "$($Entry.Name) $($run.status) — $($run.runId) $($run.error)"
}

try {
    $source = (git rev-parse HEAD).Trim()
    $tree = (git rev-parse 'HEAD^{tree}').Trim()
    if ($source -cnotmatch '^[0-9a-f]{40}$' -or $tree -cnotmatch '^[0-9a-f]{40}$' -or
        @(git status --porcelain).Count -ne 0) {
        throw 'Run F09 from an exact, clean Git source'
    }
    foreach ($anchor in $independentSources.GetEnumerator()) {
        git merge-base --is-ancestor $anchor.Value HEAD
        if ($LASTEXITCODE -ne 0) { throw "Candidate lacks $($anchor.Key) source $($anchor.Value)" }
    }
    git diff --quiet 4a6f00375d1bd04334f0c0f954a9469dd8597cb9 HEAD -- business business.lock.json
    if ($LASTEXITCODE -ne 0) { throw 'Locked business sources differ from the pre-increment baseline' }

    $runId = [Guid]::NewGuid().ToString('N')
    $output = New-Item -ItemType Directory -Path (Join-Path (Join-Path $repo 'TestResults/dev-f09') $runId)
    $runs = [Collections.Generic.List[object]]::new()
    $report = [ordered]@{
        increment = 'DEV-F09'
        runId = $runId
        source = $source
        tree = $tree
        environment = 'SYNTHETIC_NONPRODUCTION Windows LocalDB'
        independentSources = $independentSources
        suite = $Suite
        status = 'NOT_RUN'
        steps = $runs
    }
    try {
        $model = Invoke-Run 'E-SIM-01' 'python' @('tools/validate_e_sim_01.py')
        if ($model.status -ne 'PASS') { throw $model.error }
        if (!$ManifestOnly) {
            foreach ($name in $envNames) {
                $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            }
            $env:GITHUB_RUN_ID = "dev-f09-$runId"
            $env:GITHUB_RUN_ATTEMPT = '1'
            $env:GITHUB_SHA = $source
            $env:APP02A4_TEST_RUN_ID = "app02a4-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
            $env:APP02A4_SOURCE_HEAD_SHA = $source

            $localdb = Invoke-Run 'localdb-start' 'sqllocaldb' @('start', 'MSSQLLocalDB')
            if ($localdb.status -ne 'PASS') { throw $localdb.error }
            $catalogBefore = @(Get-LocalDbCatalog)
            $install = Invoke-Run 'web-install' 'npm' @('--prefix', 'web', 'ci')
            if ($install.status -ne 'PASS') { throw $install.error }
            $null = Invoke-Run 'web-build' 'npm' @('--prefix', 'web', 'run', 'build')
            $null = Invoke-Run 'web-unit' 'npm' @('--prefix', 'web', 'test')
            $null = Invoke-Run 'web-pwa' 'npm' @('--prefix', 'web', 'run', 'test:pwa-build')
            $net = Invoke-Run 'dotnet' 'dotnet' @('test', '--solution', 'FmcgDms.slnx', '-c', 'Release', '--', '--report-trx',
                '--report-trx-filename', "dev-f09-$runId.trx")
            $trx = @(Get-ChildItem -LiteralPath TestResults -Recurse -File -Filter "dev-f09-$runId.trx")
            if ($trx.Count -eq 1) { $net.receipts += Get-Receipt $trx[0].FullName }
            else { $net.status = 'FAIL'; $net.error = 'Expected the source run TRX' }

            if ($Suite -in @('All', 'Foundation')) {
                $a4 = Invoke-Run 'A.4' 'pwsh' @('-NoProfile', '-File', './scripts/Verify-App02A4.ps1')
                $a4.runId = $env:APP02A4_TEST_RUN_ID
                $a4.expected = 24
                $a4.actual = 0
                if ($a4.status -eq 'PASS') { $a4.actual = 24 }
                foreach ($entry in $foundation) { Receive-Scenario $entry $true }
            }
            if ($Suite -in @('All', 'Functional')) {
                foreach ($entry in $functional) { Receive-Scenario $entry $false }
            }
            $catalogAfter = @(Get-LocalDbCatalog)
            $report.catalog = @{
                before = $catalogBefore.Count
                after = $catalogAfter.Count
                matches = @(Compare-Object $catalogBefore $catalogAfter).Count -eq 0
            }
            if (!$report.catalog.matches) { throw 'LocalDB catalog changed during the regression run' }
        }
        $report.status = if ($ManifestOnly) { 'MODEL_ONLY' } elseif ($runs.status -contains 'FAIL') { 'FAIL' } else { 'PASS' }
    } catch {
        $report.status = 'FAIL'
        $report.error = $_.Exception.Message
    } finally {
        $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
        Write-Host "DEV-F09 $($report.status) — $($output.FullName)"
    }
    if ($report.status -eq 'FAIL') { throw 'DEV-F09 evidence is incomplete or failed. Read run.json and step logs.' }
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
