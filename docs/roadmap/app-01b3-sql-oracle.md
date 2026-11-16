# APP-01B.3 — AC-03 T0–T2 SQL-backed 39/39 oracle

**Implementation status:** executable verification and CI gate added; independent review and **exact PR-HEAD CI evidence pending** until a successful workflow run is inspected. Do not claim `39/39 PASS` or `APP-01B SYNTHETIC_VERIFIED` based only on source changes.

## Oracle, provenance, and scope

- **Expected**: immutable `business/reference-data/expected-results/golden-oracles.json#AC-03`, columns `step, mainQty, tripQty, soldQty, mainCost, tripCost, cogs, aHold, bQuota, arA, arB, custody, fund`. Compare the first **three** rows, **13** columns each, including step identity, exactly with `decimal`; no rounding, skipped cells, or copied expected values as actual.
- **Actual**: `App01b3GoldenOracleTests` reads per-location `PhysicalStockBalance` and `PhysicalStockOpening`, Trip/manifest/source/command states, `StockRight` allocations, and source-linked `LedgerEffect` **directly from SQL Server LocalDB** at T0, after `LoadManifest` T1, and after durable `B31/15/465` rejection T2. `soldQty` for this pre-sale slice is SQL opening minus both physical location balances; unexpected source kinds and financial effects are independently rejected (no sale is implemented). `arA/arB`, `cogs`, `custody`, `fund` are read from persisted ledger effects, not fixture constants.
- **Isolation**: two separately nonce-created, marked and safely dropped `tenant-a`/`tenant-b` LocalDB databases. Each must independently match **39/39**, i.e. **78 validated cells** across tenants. A-load cannot change B's T0, and B-load cannot change A's T1.
- **Business invariants**: seed physical MAIN 100/1000 and vehicle 0/0 independently of 70 allocated `StockPool`. Reserve A40, grant B30, confirm A, stage two source-backed manifest lines. T1 transfers 70/700 with two physical movement rows and four net-zero PHYSICAL_STOCK ledger legs; A/B rights remain 40/30, no AR/COGS/cash/custody/fund/sale. T2 is a durable `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT`, has no business source/receipt/effect, and leaves T1 unchanged. Repeat B31 returns replay; changed-payload B30 yields conflict without effects. The independent APP-01B.2 LocalDB suite additionally covers SQL-injected rollback + same-key retry, concurrent double-load, missing physical opening, tampered source/right, roles and cross-tenant negative tests.
- **Fail-closed comparison**: an extra unit test deliberately corrupts numeric and step identity data and requires two detected diffs. A schema mismatch, missing SQL rows, missing/repeated expected fields, non-physical effect, different numeric value or absent evidence fails CI.

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

For sign-off, record **PR head SHA, CI run URL, TestRunId, both JSON artifact links, test count and independent review findings** in the PR. An earlier green run is superseded if the PR source changes; rerun exact HEAD and re-review. A successful synthetic APP-01B.3 still does **not** verify full AC-03 **91/91** (T3–T6), APP-01C–E, real B_ONLINE/offline, production, EV/P0, invoice or go-live. The 125% display scenario remains an Owner-accepted **NOT_TESTED** exception, not a PASS.

See [APP-01B scope](app-01b-manifest-transfer-proposal.md), [APP-01B.2 command verification](app-01b2-manifest-commands.md), and [Owner decisions](../decisions/owner-app-2026-11-15.md).
