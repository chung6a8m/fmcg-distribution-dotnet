# APP-01E.3 fail-closed physical SQL evidence verifier (CI-only).
$ErrorActionPreference = 'Stop'
$matrix = [ordered]@{
  reserve = @('businesscommand-insert','stockpool-update','businesssource-insert','stockright-insert','ledgereffect-insert','businesscommand-update')
  quota = @('stockpool-update','bquota-insert','ledgereffect-insert')
  confirm = @('aorder-update','aconfirmedlinesnapshot-insert')
  credit = @('bcreditpool-update','bcreditgrant-insert','ledgereffect-insert')
  manifest = @('tripmanifest-update','physicalstockbalance-update','tripmanifestline-update','physicalstockmovement-insert','ledgereffect-insert')
  b20 = @('stockright-update','physicalstockbalance-update','saledelivery-insert','receivableopenitem-insert','cashreceipt-insert','receiptallocation-insert','bgrantconsumption-insert','ledgereffect-insert')
  a25 = @('stockright-update','physicalstockbalance-update','receivableopenitem-insert','aorder-update','ledgereffect-insert')
  offer = @('cashhandoveroffer-insert')
  accept = @('cashhandoveracceptance-insert','ledgereffect-insert')
  unload = @('tripunload-insert','tripunloadline-insert','stockright-update','stockpool-update','physicalstockbalance-update','trip-update','ledgereffect-insert')
}
$fields = @('WarehouseQty','WarehouseCost','VehicleQty','VehicleCost','PoolAllocated',
            'AGranted','BGranted','AConsumed','BConsumed','BReleased','ArA','ArB',
            'Cogs','Custody','Fund','ReceiptApplied','ARows','BRows','Offers',
            'Acceptances','Unloads','ManifestsLoaded','InvoiceEligible')
$reports = @(Get-ChildItem tests -Recurse -File -Filter '*-rollback.json' |
  Where-Object { $_.FullName -like '*TestResults*APP-01E3*' })
if ($reports.Count -ne 84) { throw "Expected 84 APP-01E.3 reports; found $($reports.Count)" }
$id = $env:APP01E3_TEST_RUN_ID
$head = $env:APP01E3_SOURCE_HEAD_SHA
$workflowSha = $env:GITHUB_SHA
if ($id -cnotmatch '^app01e3-[0-9]+-[0-9]+$' -or
    $head -cnotmatch '^[0-9a-f]{40}$' -or
    $workflowSha -cnotmatch '^[0-9a-f]{40}$') {
  throw 'E.3 CI provenance was not supplied'
}
$checked = 0
foreach ($tenant in @('tenant-a','tenant-b')) {
  foreach ($scenario in $matrix.Keys) {
    foreach ($point in $matrix[$scenario]) {
      $case = "$scenario-$point"
      $name = "$tenant-$case-rollback.json"
      $found = @($reports | Where-Object { $_.Name -ceq $name })
      if ($found.Count -ne 1) { throw "Missing/duplicate APP-01E.3 report $name" }
      $r = Get-Content $found[0].FullName -Raw | ConvertFrom-Json
      if ($r.Schema -cne 'APP01E3_ROLLBACK_V1' -or $r.Status -cne 'PASS' -or
          $r.TestRunId -cne $id -or $r.SourceHeadSha -cne $head -or
          $r.WorkflowSha -cne $workflowSha -or $r.TenantId -cne $tenant -or
          $r.Scenario -cne $scenario -or $r.FaultPoint -cne $case -or
          $r.ExpectedCells -ne 69 -or $r.MatchedCells -ne 69 -or
          @($r.Cells).Count -ne 69 -or $r.Command.ProducerId -cne 'app01e3-rollback' -or
          $r.Command.CommandId -cnotmatch '^[0-9a-f]{32}$' -or
          $r.Command.SourceNumber -cnotmatch '^[0-9a-f]{32}$' -or
          $r.FaultOutcome -cnotin @('HTTP_500','SQL_51063')) {
        throw "Bad APP-01E.3 schema/provenance/command identity: $name"
      }
      $split = $point.LastIndexOf('-')
      if ($split -le 0 -or
          $r.FaultTable.ToLowerInvariant() -cne $point.Substring(0,$split) -or
          $r.FaultOperation.ToLowerInvariant() -cne $point.Substring($split+1)) {
        throw "Wrong APP-01E.3 SQL fault target: $name"
      }
      if ($r.Database -cnotmatch '^Fmcg_App00_[0-9a-f]{32}_[AB]$' -or
          ($tenant -eq 'tenant-a' -and !$r.Database.EndsWith('_A')) -or
          ($tenant -eq 'tenant-b' -and !$r.Database.EndsWith('_B'))) {
        throw "Wrong APP-01E.3 nonce database: $name"
      }
      foreach ($stage in @('Before','AfterFault','AfterCommit','AfterReplay','AfterConflict')) {
        $s = $r.$stage
        if ($s.Database -cne $r.Database -or $s.Tables -lt 25 -or
            $s.Rows -le 0 -or $s.Sha256 -cnotmatch '^[0-9A-F]{64}$') {
          throw "Missing APP-01E.3 physical SQL snapshot: $name $stage"
        }
      }
      if ($r.Before.Sha256 -cne $r.AfterFault.Sha256 -or
          $r.Before.Rows -ne $r.AfterFault.Rows -or
          $r.AfterCommit.Sha256 -ceq $r.Before.Sha256 -or
          $r.AfterCommit.Rows -le $r.Before.Rows -or
          $r.AfterCommit.Sha256 -cne $r.AfterReplay.Sha256 -or
          $r.AfterCommit.Sha256 -cne $r.AfterConflict.Sha256 -or
          $r.AfterCommit.Rows -ne $r.AfterReplay.Rows -or
          $r.AfterCommit.Rows -ne $r.AfterConflict.Rows -or
          $r.SqlLineage.PreCommit -cne $r.SqlLineage.RolledBack -or
          $r.SqlLineage.Applied -cne $r.SqlLineage.Replayed -or
          $r.SqlLineage.Applied -cne $r.SqlLineage.Conflicted) {
        throw "Partial commit or altered replay/conflict SQL: $name"
      }
      $audit = $r.Retry.Audit
      $first = $r.Retry.Outcome
      $replay = $r.Replay.Outcome
      $conflict = $r.Conflict.Outcome
      if ($r.Retry.Http -ne 200 -or $first.Status -cne 'APPLIED' -or
          $first.Replayed -or !$first.SourceId -or
          $first.SourceId -cne $audit.SourceId -or $audit.Status -cne 'APPLIED' -or
          $audit.SourceKind -cne $r.Command.SourceKind -or
          $audit.SourceNumber -cne $r.Command.SourceNumber -or
          $audit.SourceActor -cne $r.Command.Actor -or
          $audit.CommandActor -cne $r.Command.Actor -or
          $audit.Lines -ne $r.Command.ExpectedLines -or
          $audit.Effects -ne $r.Command.ExpectedEffects -or
          $r.Replay.Http -ne 200 -or !$replay.Replayed -or
          $replay.Status -cne $first.Status -or
          $replay.OutcomeCode -cne $first.OutcomeCode -or
          $replay.SourceId -cne $first.SourceId -or
          $r.Conflict.Http -ne 409 -or $conflict.Status -cne 'CONFLICT' -or
          $conflict.Replayed -or $conflict.SourceId -or
          $conflict.ReasonCode -cne 'IDEMPOTENCY_PAYLOAD_MISMATCH') {
        throw "APP-01E.3 source/safe retry mismatch: $name"
      }
      foreach ($stage in @('ROLLBACK','REPLAY','CONFLICT')) {
        foreach ($field in $fields) {
          $key = "$stage/$field"
          $cell = @($r.Cells | Where-Object { $_.Field -ceq $key })
          if ($cell.Count -ne 1 -or !$cell[0].Match -or
              $cell[0].Actual -ne $cell[0].Expected -or $cell[0].Diff -ne 0) {
            throw "APP-01E.3 signed SQL actual/diff mismatch: $name $key"
          }
          $checked++
        }
      }
      Write-Host "APP-01E.3 $name 69/69 SQL cells PASS"
    }
  }
}
if ($checked -ne 5796) { throw "Expected 5796 SQL actual/diff cells; found $checked" }
Write-Host "APP-01E.3 complete: 84 tenant SQL rollback reports, $checked validated cells"
