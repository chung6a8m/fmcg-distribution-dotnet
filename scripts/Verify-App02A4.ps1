# APP-02A.4 -- independent, fail-closed physical SQL oracle validator.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixturePath = 'tests/FmcgDms.Api.Tests/Fixtures/app02a4-expected.json'
$expected = Get-Content $fixturePath -Raw | ConvertFrom-Json
# Raw checkout hash is used ONLY to bind C# SQL reports for this run.
# GitHub Windows checkout may convert LF to CRLF, so independently pin the
# canonical Git-blob text normalized to LF, not platform-specific disk bytes.
$expectedHash = (Get-FileHash $fixturePath -Algorithm SHA256).Hash
$normalizedFixture = (Get-Content $fixturePath -Raw -Encoding utf8).Replace("`r`n", "`n")
$normalizedFixtureHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($normalizedFixture)))
$frozenFixtureHash = '8A38FEF7EA95156A4C0DD6282BE99F47840B0FAD8086813151AF70B0D9E7E7A1'
if ($normalizedFixtureHash -cne $frozenFixtureHash) {
    throw "A4 canonical LF frozen fixture SHA drift: $normalizedFixtureHash"
}
function Test-A4Cell {
    param($Cell, [string]$Metric, [decimal]$Expected)
    try {
        $actual = [decimal]$Cell.Actual
        $difference = [decimal]$Cell.Difference
        return ($Cell.Metric -ceq $Metric -and $Cell.Match -eq $true -and
            [decimal]$Cell.Expected -eq $Expected -and
            $actual -eq $Expected -and
            $difference -eq ($actual - $Expected) -and $difference -eq 0)
    }
    catch { return $false }
}
$columns = @('PReceipts','CReceipts','Pending','Applied','CaseState','Rejected',
'Resolutions','Applications','Acceptances','BoundSources','CustodyLegs',
'CustodyAmount','FundLegs','FundAmount','OfferEffects','Outbox','Consumers',
'Acked','Retries','CaseRows','Dispositions','Corrections','CorrectionEffects','BrokenLinks')
$groups = @(1..12 | ForEach-Object { 'A11-{0:D2}' -f $_ })
$id = "app02a4-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$source = $env:APP02A4_SOURCE_HEAD_SHA
$workflow = $env:GITHUB_SHA
if ($source -cnotmatch '^[0-9a-f]{40}$' -or $workflow -cnotmatch '^[0-9a-f]{40}$') {
    throw 'APP02A4 missing exact source/workflow SHA'
}
if ($expected.schema -cne 'APP02A4_EXPECTED_V1' -or $expected.scope -cne 'SYNTHETIC_NONPRODUCTION' -or
    $expected.unit -cne 'SYNTHETIC_UNITLESS' -or $expected.invoiceEligible -ne $false -or
    $expected.amount -cne '200.0000' -or (@($expected.columns) -join '|') -cne ($columns -join '|') -or
    @($expected.groups.PSObject.Properties).Count -ne 12) {
    throw 'APP02A4 frozen scenario oracle contract drift'
}
$files = @(Get-ChildItem tests -Recurse -File -Filter '*-oracle.json' |
  Where-Object { $_.FullName -match '[\\/]TestResults[\\/]APP-02A4[\\/]' })
if ($files.Count -ne 24) { throw "Expected 24 A4 SQL reports, found $($files.Count)" }
$groupCount = 0
$checkpointCount = 0
$cellCount = 0
$negativeSentinels = 0
$physical = @()
foreach ($tenant in @('tenant-a','tenant-b')) {
    $slot = if ($tenant -ceq 'tenant-a') {'A'} else {'B'}
    $db = $null
    foreach ($group in $groups) {
        $name = "$tenant-$group-oracle.json"
        $found = @($files | Where-Object { $_.Name -ceq $name })
        if ($found.Count -ne 1) { throw "Missing/duplicate A4 report $name" }
        $r = Get-Content $found[0].FullName -Raw | ConvertFrom-Json
        if ($r.Schema -cne 'APP02A4_SQL_ORACLE_V1' -or $r.Status -cne 'PASS' -or $r.Failure -or
            $r.Group -cne $group -or $r.TenantId -cne $tenant -or
            $r.TestRunId -cne $id -or $r.SourceHeadSha -cne $source -or
            $r.WorkflowSha -cne $workflow -or $r.OracleSha256 -cne $expectedHash -or
            $r.Database -cnotmatch "^Fmcg_App00_[a-f0-9]{32}_$slot$" -or
            $r.PeerBeforeSha256 -cnotmatch '^[A-F0-9]{64}$' -or
            $r.PeerAfterSha256 -cne $r.PeerBeforeSha256) {
            throw "A4 provenance, SQL database or peer fingerprint failure: $name"
        }
        if ($null -eq $db) { $db = $r.Database }
        if ($db -cne $r.Database) { throw "A4 tenant switched physical SQL database: $name" }
        $opposite = if ($slot -eq 'A') {'B'} else {'A'}
        $peer = $db.Substring(0, $db.Length - 1) + $opposite
        if ($r.PeerDatabase -cne $peer) { throw "A4 peer DB mismatch: $name" }
        $phases = @($expected.groups.PSObject.Properties[$group].Value.PSObject.Properties | ForEach-Object { $_.Name })
        if ($phases.Count -lt 1 -or @($r.Checkpoints).Count -ne $phases.Count -or
            $r.CheckedCells -ne ($phases.Count * 24) -or $r.MatchedCells -ne $r.CheckedCells) {
            throw "A4 checkpoint count or metric count mismatch: $name"
        }
        for ($i = 0; $i -lt $phases.Count; $i++) {
            $phase = $phases[$i]
            $s = $r.Checkpoints[$i]
            if ($s.Phase -cne $phase -or $s.Database -cne $db -or
                $s.TripId -cnotmatch '^[a-fA-F0-9-]{36}$' -or
                $s.SqlSha256 -cnotmatch '^[A-F0-9]{64}$' -or
                $s.SqlTables -lt 25 -or $s.SqlRows -lt 1 -or
                @($s.SqlStatusHistory).Count -lt 1 -or @($s.Cells).Count -ne 24) {
                throw "A4 missing physical SQL checkpoint witness: $name / $phase"
            }
            $state = [string]$expected.groups.PSObject.Properties[$group].Value.$phase
            $want = @($expected.states.PSObject.Properties[$state].Value)
            if ($want.Count -ne 24) { throw "A4 missing frozen state $state" }
            # Verify actual persisted lifecycle is not an invented/empty history.
            # C receipts may have earlier states; every current nonzero
            # terminal/pending count MUST have a matching history transition.
            $history = @($s.SqlStatusHistory)
            $legal = @('RECEIVED','PENDING_DEPENDENCY','APPLIED','CASE','REJECTED')
            foreach ($status in $history) {
                if ($legal -cnotcontains $status) {
                    throw "A4 illegal persisted history status: $name / $phase / $status"
                }
            }
            foreach ($pair in @(@('PENDING_DEPENDENCY',2),@('APPLIED',3),@('CASE',4),@('REJECTED',5))) {
                if ([decimal]$want[[int]$pair[1]] -gt 0 -and
                    $history -cnotcontains [string]$pair[0]) {
                    throw "A4 missing required persisted history: $name / $phase / $($pair[0])"
                }
            }
            for ($j = 0; $j -lt 24; $j++) {
                $cell = $s.Cells[$j]
                $e = [decimal]$want[$j]
                if (!(Test-A4Cell $cell $columns[$j] $e)) {
                    throw "A4 expected/actual/diff mismatch: $name / $phase / $($columns[$j])"
                }
                # Mutation sentinels exercise the SAME validator used for
                # normal evidence: each corrupted field must be rejected.
                if ($tenant -ceq 'tenant-a' -and $group -ceq 'A11-01' -and
                    $i -eq 0 -and $j -eq 0) {
                    $original = $cell | ConvertTo-Json -Depth 4 -Compress
                    foreach ($field in @('Metric','Expected','Actual','Difference','Match')) {
                        $mutant = $original | ConvertFrom-Json
                        switch ($field) {
                            'Metric' { $mutant.Metric = '__MUTATED__' }
                            'Expected' { $mutant.Expected = [decimal]$mutant.Expected + 1 }
                            'Actual' { $mutant.Actual = [decimal]$mutant.Actual + 1 }
                            'Difference' { $mutant.Difference = [decimal]$mutant.Difference + 1 }
                            'Match' { $mutant.Match = $false }
                        }
                        if (Test-A4Cell $mutant $columns[$j] $e) {
                            throw "A4 validator accepted tampered $field witness"
                        }
                        $negativeSentinels++
                    }
                }
                $cellCount++
            }
            $checkpointCount++
        }
        $groupCount++
    }
    $physical += $db
    Write-Host "APP-02A.4 $($tenant): 12 groups independently checked"
}
if ($physical[0] -ceq $physical[1] -or
    $physical[0].Substring(0, $physical[0].Length - 1) -cne
      $physical[1].Substring(0, $physical[1].Length - 1) -or
    $groupCount -ne 24 -or $checkpointCount -ne 60 -or $cellCount -ne 1440 -or
    $negativeSentinels -ne 5) {
    throw "A4 requires 24 groups, 60 checkpoints and 1440 physical SQL cells, observed $groupCount/$checkpointCount/$cellCount"
}
Write-Host "APP-02A.4 PASS: 24/24 per-tenant groups, 60/60 checkpoints, 1440/1440 SQL cells, peer full-table digest stable, 5/5 validator mutations rejected, TestRunId=$id, source HEAD=$source"
