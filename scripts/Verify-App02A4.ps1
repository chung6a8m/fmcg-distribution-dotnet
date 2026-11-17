# APP-02A.4 -- independent, fail-closed physical SQL oracle validator.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixturePath = 'tests/FmcgDms.Api.Tests/Fixtures/app02a4-expected.json'
$expected = Get-Content $fixturePath -Raw | ConvertFrom-Json
$expectedHash = (Get-FileHash $fixturePath -Algorithm SHA256).Hash
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
            for ($j = 0; $j -lt 24; $j++) {
                $cell = $s.Cells[$j]
                $e = [decimal]$want[$j]
                $a = [decimal]$cell.Actual
                if ($cell.Metric -cne $columns[$j] -or !$cell.Match -or
                    [decimal]$cell.Expected -ne $e -or $a -ne $e -or
                    [decimal]$cell.Difference -ne ($a - $e) -or [decimal]$cell.Difference -ne 0) {
                    throw "A4 expected/actual/diff mismatch: $name / $phase / $($columns[$j])"
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
    $groupCount -ne 24 -or $checkpointCount -ne 56 -or $cellCount -ne 1344) {
    throw "A4 requires 24 groups, 56 checkpoints and 1344 physical SQL cells, observed $groupCount/$checkpointCount/$cellCount"
}
Write-Host "APP-02A.4 PASS: 24/24 per-tenant groups, 56/56 checkpoints, 1344/1344 SQL cells, peer full-table digest stable, TestRunId=$id, source HEAD=$source"
