# APP-01B.3 — AC-03 T0–T2 SQL-backed 39/39 oracle

**Status (2026-11-16): SQL_ORACLE_VERIFIED; APP-01B closeout subject to reviewed final-HEAD CI and PR #27 merge.** [PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27) is the authoritative record of final source SHA, independent review, CI and tenant actual/diff artifacts. The [APP-01B closeout](../closeout/2026-11-16-app-01b.md) covers acceptance and explicit exclusions.

## Oracle, provenance, and scope

- **Expected**: immutable `business/reference-data/expected-results/golden-oracles.json#AC-03`, columns `step, mainQty, tripQty, soldQty, mainCost, tripCost, cogs, aHold, bQuota, arA, arB, custody, fund`. Compare the first **three** rows, **13** columns each, including step identity, exactly with `decimal`; no rounding, skipped cells, or copied expected values as actual.
- **Actual**: `App01b3GoldenOracleTests` reads per-location `PhysicalStockBalance` and `PhysicalStockOpening`, Trip/manifest/source/command states, `StockRight` allocations, and source-linked `LedgerEffect` **directly from SQL Server LocalDB** at T0, after `LoadManifest` T1, and after durable `B31/15/465` rejection T2. `soldQty` for this pre-sale slice is SQL opening minus both physical location balances; unexpected source kinds and financial effects are independently rejected (no sale is implemented). `arA/arB`, `cogs`, `custody`, `fund` are read from persisted ledger effects, not fixture constants.
- **Isolation**: two separately nonce-created, marked and safely dropped `tenant-a`/`tenant-b` LocalDB databases. Each must independently match **39/39**, i.e. **78 validated cells** across tenants. A-load cannot change B's T0, and B-load cannot change A's T1.
- **Business invariants**: seed physical MAIN 100/1000 and vehicle 0/0 independently of 70 allocated `StockPool`. Reserve A40, grant B30, confirm A, stage two source-backed manifest lines. T1 transfers 70/700 with two physical movement rows and four net-zero PHYSICAL_STOCK ledger legs; A/B rights remain 40/30, no AR/COGS/cash/custody/fund/sale. T2 is a durable `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT`, has no business source/receipt/effect, and leaves T1 unchanged. Repeat B31 returns replay; changed-payload B30 yields conflict without effects. The independent APP-01B.2 LocalDB suite additionally covers SQL-injected rollback + same-key retry, concurrent double-load, missing physical opening, tampered source/right, roles and cross-tenant negative tests.
- **Fail-closed comparison**: a unit mutation sentinel corrupts numeric and step identity data; an additional rights-ledger mutation sentinel proves T2 no-effects detects a changed full-trip SQL ledger fingerprint even when golden cells remain equal. The test captures all ledger rows including RIGHT_A/RIGHT_B, enforces 2 rights legs at T0 and 6 total legs after T1, and checks zero BusinessSource rows linked to the rejected command regardless of TripId. Schema mismatch, missing SQL rows, unexpected effects or absent evidence fails CI.

## Reproducible execution and evidence

On **Windows / SQL Server LocalDB**, from repository root:

```powershell
sqllocaldb start MSSQLLocalDB
./scripts/Verify-Business.ps1
python tools/validate_business.py
dotnet restore FmcgDms.slnx
dotnet build FmcgDms.slnx -c Release --no-restore
$env:APP00A_SQL_SERVER = '(localdb)\MSSQLLocalDB'
dotnet test --solution FmcgDms.slnx -c Release --no-build --minimum-expected-tests 64 -- --report-trx --coverage --coverage-output-format cobertura
```

CI passes `APP01B3_TEST_RUN_ID=app01b3-<run_id>-<attempt>` and the **source PR HEAD SHA** separately from the synthetic workflow merge SHA. Each tenant writes `TestResults/APP-01B3/<tenant>-oracle.json` relative to the test assembly output. Reports include the nonce DB name, TestRunId, source/workflow SHAs, baseline SHA-256, 3 persistence snapshots, every expected/actual/diff and PASS only when **39 exact matches** and the whole test completes. CI independently requires both reports to exist, match 39/39 with no differences, and have matching run, source and baseline identity; the existing TRX/Cobertura/Playwright artifacts are retained. Do not commit transient run output to the repository.

### CI chronology and final evidence

- Initial [CI #43816874773](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43816874773) on source HEAD `02449ee219737954c507002b13ff653005e8508c` was **SUCCESS**: 64/64 tests, two tenant actual/diff reports at 39/39, source/workflow SHA separation, LocalDB/Business Baseline/Playwright gates. TestRunId `app01b3-43816874773-1`; [test artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43816874773/artifacts/14202926795) and [browser artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43816874773/artifacts/14202538904).
- Independent review subsequently found P2: rights-only ledger changes could evade the old T2 no-effects assertion. The PR adds a full-trip ledger row fingerprint, direct rejected-command source count and mutation sentinel. **The earlier run is superseded by these changes**; only the final reviewed-HEAD CI and review outcome linked from [PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27) qualify for merge/closeout.

Sign-off requires **final reviewed PR head SHA, CI run URL, TestRunId, both JSON artifact links, test count and independent review findings** in [PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27). A prior green run cannot substitute for an exact-HEAD rerun and re-review. A successful synthetic APP-01B.3 still does **not** verify full AC-03 **91/91** (T3–T6), APP-01C–E, real B_ONLINE/offline, production, EV/P0, invoice or go-live. The 125% display scenario remains an Owner-accepted **NOT_TESTED** exception, not a PASS.

See [APP-01B scope](app-01b-manifest-transfer-proposal.md), [APP-01B.2 command verification](app-01b2-manifest-commands.md), and [Owner decisions](../decisions/owner-app-2026-11-15.md).
