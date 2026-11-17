# APP-01E.4 fail-closed physical SQL and restart evidence gate (Windows LocalDB only).
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem tests -Recurse -File -Filter '*-recovery.json' |
  Where-Object { $_.FullName -like '*TestResults*APP-01E4*' })
if ($reports.Count -ne 10) { throw "E.4 requires exactly 10 physical TCP/restart reports, found $($reports.Count)" }

$id = "app01e4-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$sourceHead = $env:APP01E4_SOURCE_HEAD_SHA
if ([string]::IsNullOrWhiteSpace($sourceHead)) { throw 'E.4 source HEAD was not provided' }
if ($sourceHead -cnotmatch '^[a-f0-9]{40}$' -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$') {
  throw 'E.4 source or workflow SHA malformed'
}

$expected = @{
  'quota'  = @{ Kind='STOCK_ALLOC'; Lines=1; Effects=1; Actor='e2-warehouse'; Status='APPLIED' }
  'b20'    = @{ Kind='B_SALE_DELIVERY'; Lines=1; Effects=7; Actor='e2-sales'; Status='APPLIED' }
  'accept' = @{ Kind='CASH_HANDOVER_ACCEPT'; Lines=1; Effects=2; Actor='e2-cashier'; Status='APPLIED' }
  'unload' = @{ Kind='TRIP_UNLOAD'; Lines=2; Effects=5; Actor='e2-warehouse'; Status='APPLIED' }
  'reject' = @{ Kind=''; Lines=0; Effects=0; Actor='e2-warehouse'; Status='REJECTED' }
}
$fields = @('WarehouseQty','WarehouseCost','VehicleQty','VehicleCost','PoolAllocated',
  'AGranted','BGranted','AConsumed','BConsumed','BReleased','ArA','ArB','Cogs',
  'Custody','Fund','ReceiptApplied','ARows','BRows','Offers','Acceptances','Unloads',
  'ManifestsLoaded','InvoiceEligible')
if ($fields.Count -ne 23) { throw 'APP-01E.4 SQL probe field list drift' }

$total = 0
foreach ($tenant in @('tenant-a','tenant-b')) {
  $slot = if ($tenant -ceq 'tenant-a') { 'A' } else { 'B' }
  foreach ($case in @('quota','b20','accept','unload','reject')) {
    $name = "$tenant-$case-recovery.json"
    $found = @($reports | Where-Object { $_.Name -ceq $name })
    if ($found.Count -ne 1) { throw "Missing or duplicate APP-01E.4 report $name" }
    $r = Get-Content -LiteralPath $found[0].FullName -Raw | ConvertFrom-Json
    $target = $expected[$case]
    if ($r.Schema -cne 'APP01E4_RECOVERY_V1' -or $r.Status -cne 'PASS' -or
        $r.TestRunId -cne $id -or $r.SourceHeadSha -cne $sourceHead -or
        $r.WorkflowSha -cne $env:GITHUB_SHA -or $r.TenantId -cne $tenant -or
        $r.Scenario -cne $case -or $r.Database -cnotmatch "^Fmcg_App00_[a-f0-9]{32}_$slot$" -or
        $r.Committed.Database -cne $r.Database -or $r.Posted.Database -cne $r.Database -or
        $r.Sql.CommandActor -cne $r.ActorId -or $r.ActorId -cne $target.Actor -or
        $r.ProducerId -cne 'app01e4-recovery' -or
        $r.CommandId -cnotmatch '^[a-f0-9]{32}$' -or
        $r.SourceNumber -cnotmatch '^[a-f0-9]{32}$') {
      throw "E.4 schema, ownership, lineage or exact source HEAD mismatch: $name"
    }
    if ($r.Transport -cne 'TCP_RST_AFTER_UPSTREAM_RESPONSE_NO_DOWNSTREAM_ACK' -or
        $r.UpstreamStatus -cnotmatch '^HTTP/1\.1 200' -or
        $r.Restart -cne 'TWO_DISTINCT_OS_PROCESSES' -or
        $r.FirstProcessId -le 0 -or $r.SecondProcessId -le 0 -or
        $r.FirstProcessId -eq $r.SecondProcessId) {
      throw "E.4 report lacks actual transport loss or distinct process restart: $name"
    }
    foreach ($hidden in @($r.OtherActorLookup,$r.OtherTenantLookup)) {
      if ($hidden.State -cne 'UNKNOWN' -or $hidden.ReasonCode -cne 'NOT_FOUND' -or
          $null -ne $hidden.SourceId -or $null -ne $hidden.Status -or
          $null -ne $hidden.OutcomeCode) {
        throw "E.4 actor/tenant isolation leaked an outcome: $name"
      }
    }
    $o = $r.OutcomeLookup
    if ($o.State -cne 'COMPLETED' -or $o.Status -cne $target.Status -or
        $o.Status -cne $r.Sql.Status -or $o.OutcomeCode -cne $r.Sql.OutcomeCode -or
        $o.ReasonCode -cne $r.Sql.ReasonCode -or $o.SourceId -cne $r.Sql.SourceId -or
        $r.Sql.Lines -ne $target.Lines -or $r.Sql.Effects -ne $target.Effects -or
        $r.ExpectedSourceKind -cne $target.Kind -or $r.ExpectedLines -ne $target.Lines -or
        $r.ExpectedEffects -ne $target.Effects) {
      throw "E.4 outcome differs from independently persisted SQL: $name"
    }
    if ($case -eq 'reject') {
      if ($o.OutcomeCode -cne 'REJECTED' -or $o.ReasonCode -cne 'QUANTITY_INVALID' -or
          $null -ne $o.SourceId -or $null -ne $r.Sql.SourceKind -or
          $null -ne $r.Sql.SourceNumber -or $null -ne $r.Sql.SourceActor) {
        throw "E.4 durable rejection was interpreted as an applied write: $name"
      }
    } else {
      if ($null -eq $o.SourceId -or $r.Sql.SourceKind -cne $target.Kind -or
          $r.Sql.SourceNumber -cne $r.SourceNumber -or
          $r.Sql.SourceActor -cne $r.ActorId -or $null -ne $o.ReasonCode) {
        throw "E.4 committed source identity/actor lineage mismatch: $name"
      }
    }
    if ($r.Replay.Replayed -ne $true -or $r.Replay.Status -cne $o.Status -or
        $r.Replay.OutcomeCode -cne $o.OutcomeCode -or
        $r.Replay.ReasonCode -cne $o.ReasonCode -or
        $r.Replay.SourceId -cne $o.SourceId -or
        $r.Conflict.Status -cne 'CONFLICT' -or
        $r.Conflict.ReasonCode -cne 'IDEMPOTENCY_PAYLOAD_MISMATCH') {
      throw "E.4 same-identity replay or changed-payload conflict invalid: $name"
    }
    foreach ($snapshot in @($r.Before,$r.Committed,$r.Replayed,$r.Conflicted)) {
      if ($snapshot.Database -cne $r.Database -or
          $snapshot.Sha256 -cnotmatch '^[A-F0-9]{64}$' -or
          $snapshot.Tables -le 10 -or $snapshot.Rows -le 0) {
        throw "E.4 physical full-table fingerprint missing: $name"
      }
    }
    if ($r.Before.Sha256 -ceq $r.Committed.Sha256 -or
        $r.Committed.Sha256 -cne $r.Replayed.Sha256 -or
        $r.Committed.Sha256 -cne $r.Conflicted.Sha256 -or
        $r.Posted.LedgerSha256 -cne $r.ReplaySql.LedgerSha256 -or
        $r.Posted.LedgerSha256 -cne $r.ConflictSql.LedgerSha256 -or
        $r.ExpectedCells -ne 46 -or $r.MatchedCells -ne 46 -or
        @($r.Cells).Count -ne 46 -or @($r.Cells | Where-Object { $_.Match -ne $true }).Count -ne 0) {
      throw "E.4 command/stock/ledger changed during lookup/replay/conflict: $name"
    }
    $seen = @{}
    foreach ($step in @('REPLAY','CONFLICT')) {
      $actualSql = if ($step -eq 'REPLAY') { $r.ReplaySql } else { $r.ConflictSql }
      foreach ($field in $fields) {
        $key = "$step/$field"
        $match = @($r.Cells | Where-Object { $_.Field -ceq $key })
        if ($match.Count -ne 1 -or $seen.ContainsKey($key)) {
          throw "E.4 missing or duplicated SQL actual/diff cell $name/$key"
        }
        $seen[$key] = $true
        $c = $match[0]
        if ([decimal]$c.Expected -ne [decimal]$r.Posted.Numbers.$field -or
            [decimal]$c.Actual -ne [decimal]$actualSql.Numbers.$field -or
            [decimal]$c.Diff -ne ([decimal]$c.Actual - [decimal]$c.Expected) -or
            [decimal]$c.Diff -ne 0) {
          throw "E.4 SQL actual/diff mismatch: $name/$key"
        }
        $total++
      }
    }
    Write-Host "APP-01E.4 $name: SQL 46/46, TCP RST + process restart, durable outcome recovered ($id)"
  }
}
if ($total -ne 460) { throw "Expected 460 real SQL E.4 cells; saw $total" }
Write-Host "APP-01E.4: 10/10 physical recovery reports, 460/460 SQL cells; exact source $sourceHead"
