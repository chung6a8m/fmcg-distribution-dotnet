# APP-01E.5: fail-closed T0-T6 SQL cross-transaction evidence validator.
# Reports are written by the two real LocalDB tenant integration tests.
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem tests -Recurse -File -Filter '*-reconciliation.json' |
  Where-Object { $_.FullName -like '*TestResults*APP-01E5*' })
if ($reports.Count -ne 2) { throw "E.5 requires exactly two physical tenant reports; found $($reports.Count)" }
$id = "app01e5-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$sourceHead = $env:APP01E5_SOURCE_HEAD_SHA
if ($sourceHead -cnotmatch '^[0-9a-f]{40}$' -or $env:GITHUB_SHA -cnotmatch '^[0-9a-f]{40}$') {
  throw 'E.5 exact source/workflow SHA not provided'
}
$oraclePath = 'business/reference-data/expected-results/golden-oracles.json'
$hash = (Get-FileHash $oraclePath -Algorithm SHA256).Hash
$oracle = (Get-Content $oraclePath -Raw | ConvertFrom-Json).'AC-03'
$fields = @('step','mainQty','tripQty','soldQty','mainCost','tripCost',
  'cogs','aHold','bQuota','arA','arB','custody','fund')
$steps = @('T0','T1_LOAD','T2_REJECT','T3_B20','T4_A25','T5_HANDOVER','T6_RETURN')
$sources = @(4,5,5,6,7,9,10)
$lines = @(4,6,6,7,8,10,12)
$effects = @(3,7,7,14,18,20,25)
if ($oracle.status -cne 'MODEL_ONLY' -or $oracle.unit -cne 'SYNTHETIC_UNITLESS' -or
    $oracle.invoiceEligible -ne $false -or
    (@($oracle.cols) -join '|') -cne ($fields -join '|') -or @($oracle.steps).Count -ne 7) {
  throw 'APP-01E.5 locked oracle schema or status changed'
}
$databases = @()
$cellTotal = 0
$equationTotal = 0
foreach ($tenant in @('tenant-a','tenant-b')) {
  $slot = if ($tenant -ceq 'tenant-a') { 'A' } else { 'B' }
  $name = "$tenant-reconciliation.json"
  $found = @($reports | Where-Object { $_.Name -ceq $name })
  if ($found.Count -ne 1) { throw "E.5 missing or duplicate $name" }
  $r = Get-Content -LiteralPath $found[0].FullName -Raw | ConvertFrom-Json
  if ($r.Schema -cne 'APP01E5_RECONCILIATION_V1' -or $r.Status -cne 'PASS' -or
      $r.Failure -or $r.TenantId -cne $tenant -or
      $r.ProducerId -cne 'app01e5-reconcile' -or
      $r.RejectCommandId -cnotmatch '^[0-9a-f]{32}$' -or
      $r.TestRunId -cne $id -or $r.SourceHeadSha -cne $sourceHead -or
      $r.WorkflowSha -cne $env:GITHUB_SHA -or
      $r.OracleSha256 -cne $hash -or
      $r.Database -cnotmatch "^Fmcg_App00_[a-f0-9]{32}_$slot$" -or
      $r.ExpectedCells -ne 91 -or $r.CheckedCells -ne 91 -or
      $r.MatchedCells -ne 91 -or @($r.Cells).Count -ne 91 -or
      $r.ExpectedEquations -ne 259 -or $r.CheckedEquations -ne 259 -or
      $r.MatchedEquations -ne 259 -or @($r.Stages).Count -ne 7) {
    throw "APP-01E.5 report schema/provenance/cardinality mismatch $name"
  }
  $databases += $r.Database
  for ($i = 0; $i -lt 7; $i++) {
    $s = $r.Stages[$i]
    $f = $s.Facts
    $n = $s.Sql.Numbers
    $full = $s.Full
    if ($s.Row -ne $i -or $s.Step -cne $steps[$i] -or
        $f.PersistedStep -cne $steps[$i] -or
        $s.Sql.Tenant -cne $tenant -or $s.Sql.Database -cne $r.Database -or
        $full.Database -cne $r.Database -or $full.Tables -lt 25 -or
        $full.Rows -lt 1 -or $full.Sha256 -cnotmatch '^[A-F0-9]{64}$' -or
        $s.Sql.LedgerSha256 -cnotmatch '^[A-F0-9]{64}$' -or
        @($s.Equations).Count -ne 37 -or
        $f.SourceCount -ne $sources[$i] -or
        $f.SourceLineCount -ne $lines[$i] -or
        $f.EffectCount -ne $effects[$i]) {
      throw "APP-01E.5 source/ledger/full-SQL checkpoint malformed $name row $i"
    }
    $rejectStatus = if ($i -lt 2) { 'NONE' } else { 'REJECTED' }
    $rejectOutcome = if ($i -lt 2) { 'NONE' } else { 'REJECTED_QUOTA' }
    $rejectReason = if ($i -lt 2) { 'NONE' } else { 'B_QUOTA_INSUFFICIENT' }
    if ($f.RejectStatus -cne $rejectStatus -or $f.RejectOutcome -cne $rejectOutcome -or
        $f.RejectReason -cne $rejectReason -or $f.RejectedSources -ne 0 -or
        $f.OfferLedgerEffects -ne 0 -or $f.InvoiceEligible -ne 0) {
      throw "APP-01E.5 rejected B31 or nonposting offer leaked posting: $name row $i"
    }
    $actual = [ordered]@{
      step = $f.PersistedStep; mainQty = $n.WarehouseQty; tripQty = $n.VehicleQty
      soldQty = $f.IssuedQty; mainCost = $n.WarehouseCost; tripCost = $n.VehicleCost
      cogs = $n.Cogs; aHold = $f.AHeld; bQuota = $f.BQuotaHeld
      arA = $n.ArA; arB = $n.ArB; custody = $n.Custody; fund = $n.Fund
    }
    $rowCells = @($r.Cells | Where-Object { $_.Row -eq $i })
    if ($rowCells.Count -ne 13 -or (@($actual.Keys) -join '|') -cne ($fields -join '|')) {
      throw "APP-01E.5 oracle field set invalid $name row $i"
    }
    foreach ($field in $fields) {
      $matches = @($rowCells | Where-Object { $_.Field -ceq $field })
      if ($matches.Count -ne 1) { throw "E.5 missing/duplicate oracle field $name/$i/$field" }
      $cell = $matches[0]
      if ($field -eq 'step') {
        if ($oracle.steps[$i][0] -cne $steps[$i] -or
            $cell.Expected -cne $steps[$i] -or
            $cell.Actual -cne $actual.step -or $null -ne $cell.Difference -or
            !$cell.Match) { throw "E.5 invalid step cell $name/$i" }
      } else {
        $j = [array]::IndexOf($fields,$field)
        $want = [decimal]::Parse([string]$oracle.steps[$i][$j],
          [Globalization.CultureInfo]::InvariantCulture)
        $observed = [decimal]$actual[$field]
        if ([decimal]$cell.Expected -ne $want -or
            [decimal]$cell.Actual -ne $observed -or
            [decimal]$cell.Difference -ne ($observed - $want) -or
            $observed -ne $want -or !$cell.Match) {
          throw "E.5 actual/expected/signed-diff mismatch $name/$i/$field"
        }
      }
      $cellTotal++
    }
    $equations = [ordered]@{
      'opening-qty=physical+issued' = @($f.OpeningQty,([decimal]$n.WarehouseQty + [decimal]$n.VehicleQty + [decimal]$f.IssuedQty))
      'opening-cost=physical+issued' = @($f.OpeningCost,([decimal]$n.WarehouseCost + [decimal]$n.VehicleCost + [decimal]$f.IssuedCost))
      'sale-qty=issued' = @($f.SaleQty,$f.IssuedQty)
      'cogs=issued-cost' = @($f.IssuedCost,$n.Cogs)
      'warehouse-qty=opening-load+return' = @( ([decimal]$f.OpeningQty - [decimal]$f.LoadQty + [decimal]$f.ReturnQty),$n.WarehouseQty)
      'warehouse-cost=opening-load+return' = @( ([decimal]$f.OpeningCost - [decimal]$f.LoadCost + [decimal]$f.ReturnCost),$n.WarehouseCost)
      'vehicle-qty=load-issued-return' = @( ([decimal]$f.LoadQty - [decimal]$f.IssuedQty - [decimal]$f.ReturnQty),$n.VehicleQty)
      'vehicle-cost=load-issued-return' = @( ([decimal]$f.LoadCost - [decimal]$f.IssuedCost - [decimal]$f.ReturnCost),$n.VehicleCost)
      'ar-A=receivable-outstanding' = @($f.ArAOutstanding,$n.ArA)
      'ar-B=receivable-outstanding' = @($f.ArBOutstanding,$n.ArB)
      'receipt=allocation' = @($f.CashReceived,$f.CashAllocated)
      'receipt=ledger-custody+fund' = @($f.CashReceived,([decimal]$n.Custody+[decimal]$n.Fund))
      'fund=verified-acceptance' = @($f.HandoverAccepted,$n.Fund)
      'credit-grant=source-consumption' = @($f.SourceCreditConsumed,$f.CreditConsumed)
      'B-ar=consumed-credit' = @($f.CreditConsumed,$n.ArB)
      'A-consumed=issued-A' = @($f.AStockIssuedQty,$f.ConsumedA)
      'B-consumed=issued-B' = @($f.BStockIssuedQty,$f.ConsumedB)
      'pool=granted-released' = @( ([decimal]$f.GrantedA + [decimal]$f.GrantedB - [decimal]$f.ReleasedB), $n.PoolAllocated)
      'A-held=granted-consumed' = @( ([decimal]$f.GrantedA - [decimal]$f.ConsumedA), $f.AHeld)
      'B-held=granted-consumed-released' = @( ([decimal]$f.GrantedB - [decimal]$f.ConsumedB - [decimal]$f.ReleasedB), $f.BQuotaHeld)
      'released-B=unload-source' = @($f.ReturnBReleased,$f.ReleasedB)
      'physical-ledger-qty=negative-issued' = @((-[decimal]$f.IssuedQty),$f.PhysicalLedgerQtyNet)
      'physical-ledger-cost=negative-issued' = @((-[decimal]$f.IssuedCost),$f.PhysicalLedgerCostNet)
      'sources-expected' = @($sources[$i],$f.SourceCount)
      'source-lines-expected' = @($lines[$i],$f.SourceLineCount)
      'ledger-effects-expected' = @($effects[$i],$f.EffectCount)
      'source-to-applied-command' = @($f.SourceCount,$f.LinkedAppliedSources)
      'sources-without-lines=0' = @(0,$f.SourcesWithoutLine)
      'unexpected-source-kinds=0' = @(0,$f.UnknownSourceKinds)
      'nonposting-offer-effects=0' = @(0,$f.OfferLedgerEffects)
      'rejected-source=0' = @(0,$f.RejectedSources)
      'sales-count' = @($(if ($i -lt 3) {0} elseif ($i -eq 3) {1} else {2}),$f.Sales)
      'receipt-count' = @($(if ($i -lt 3) {0} else {1}),$f.Receipts)
      'offer-count' = @($(if ($i -lt 5) {0} else {1}),$f.Offers)
      'accept-count' = @($(if ($i -lt 5) {0} else {1}),$f.Acceptances)
      'unload-count' = @($(if ($i -lt 6) {0} else {1}),$f.Unloads)
      'invoice-eligible=0' = @(0,$f.InvoiceEligible)
    }
    if ($equations.Count -ne 37) { throw "E.5 equation contract drift" }
    foreach ($key in $equations.Keys) {
      $match = @($s.Equations | Where-Object { $_.Name -ceq $key })
      if ($match.Count -ne 1) { throw "E.5 missing/duplicate equation $name/$i/$key" }
      $e = $match[0]
      $want = [decimal]$equations[$key][0]
      $observed = [decimal]$equations[$key][1]
      if ([decimal]$e.Expected -ne $want -or [decimal]$e.Actual -ne $observed -or
          [decimal]$e.Difference -ne ($observed - $want) -or
          $observed -ne $want -or !$e.Match) {
        throw "E.5 independent source/ledger equation mismatch $name/$i/$key"
      }
      $equationTotal++
    }
    if ($i -gt 0 -and $r.Stages[$i-1].Full.Sha256 -ceq $full.Sha256) {
      throw "E.5 command checkpoint did not persist a new durable SQL state $name/$i"
    }
  }
  if ($r.Stages[1].Sql.LedgerSha256 -cne $r.Stages[2].Sql.LedgerSha256 -or
      $r.Stages[1].Full.Sha256 -ceq $r.Stages[2].Full.Sha256 -or
      $r.Stages[4].Sql.Numbers.ArA -ne $r.Stages[6].Sql.Numbers.ArA -or
      $r.Stages[4].Sql.Numbers.ArB -ne $r.Stages[6].Sql.Numbers.ArB -or
      $r.Stages[4].Sql.Numbers.Cogs -ne $r.Stages[6].Sql.Numbers.Cogs -or
      $r.ReplayFullSqlSha256 -cne $r.Stages[6].Full.Sha256) {
    throw "E.5 T2 nonposting, T5/T6 financial immutability or replay full SQL mismatch $name"
  }
  Write-Host "APP-01E.5 ${name}: 91/91 SQL oracle cells; 259/259 independent reconciliation equations; replay fingerprint stable"
}
if ($databases[0] -ceq $databases[1] -or $cellTotal -ne 182 -or $equationTotal -ne 518) {
  throw "E.5 two physical tenants, 182 cells or 518 equations not independently proven"
}
Write-Host "APP-01E.5 PASS: two nonce-owned LocalDB tenants, 182/182 oracle cells, 518/518 SQL reconciliation equations; source HEAD $sourceHead"
