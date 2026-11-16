# APP-01C.4 — Independent SQL-backed AC-03 T0–T4 oracle (two tenants)

**Implementation status (2026-11-16): IMPLEMENTED / CI_AND_INDEPENDENT_REVIEW_PENDING.** This is a synthetic, nonproduction verification gate; a report is **NOT VERIFIED** until CI on the exact final reviewed PR head confirms both SQL reports and the PR is reviewed. Business Baseline `business/` and `business.lock.json` remain read-only.

## Authority, matrix and provenance

- Locked expected data: `business/reference-data/expected-results/golden-oracles.json#AC-03` with SHA-256 of the original bytes. Verify `status=MODEL_ONLY`, `unit=SYNTHETIC_UNITLESS`, `invoiceEligible=false`. Never edit or rederive expected values from implementation.
- Exactly **five ordered checkpoints**: `T0`, `T1_LOAD`, `T2_REJECT`, `T3_B20`, `T4_A25`. Each row has **13 fields including step**: `step, mainQty, tripQty, soldQty, mainCost, tripCost, cogs, aHold, bQuota, arA, arB, custody, fund`. The `decimal` comparator uses exact equality and signed `actual - expected` diff; missing, misordered or altered fields fail closed. This makes **65/65 per tenant / 130/130 across two databases**.
- Independent actuals: `App01c4GoldenOracleTests` opens each SQL Server LocalDB tenant connection directly; it does **not** compare API return bodies with golden expected values. Physical MAIN/TRIP quantities and costs are read from `PhysicalStockBalance` and related opening records; sold quantity is opening minus both balances. A hold/B quota are computed from persisted `StockRight`. COGS, AR_A, AR_B, custody and fund are aggregated from `LedgerEffect` joined to `BusinessSourceLine`/`BusinessSource`. Stage identity is independently detected from posted SQL command/manifest/sale state.
- **T0**: MAIN 100/1000; VEHICLE 0/0; A40/B30; no sale, receivable, cash, COGS or fund. **T1**: manifest load MAIN 30/300 and VEHICLE 70/700, rights unchanged. **T2**: a persisted `B31/15/465` quota rejection, no linked business source, no new ledger or physical/financial effects, same physical balances and rights. `T0–T2` still have their **separate APP-01B.3 39/39 tenant gate**.
- **T3**: independently approved synthetic B credit grant and preexisting witnessed cash evidence precede a B20 sale: TRIP 50/500; sold20; COGS200; B remaining10; AR_B100 from gross300 less verified200; custody200; FUND0.
- **T4**: A25 is sourced from original A Confirm's immutable price15 and seven-day terms; TRIP 25/250; sold45; cumulative COGS450; A remaining15; B remaining10; AR_A375, AR_B100; custody200 and fund0. The due date is checked against persisted Trip BusinessDate + frozen 7 days. A is `PARTIALLY_DELIVERED`, not a full 40-unit fulfillment.

## Independent source/ledger and negative invariants

Besides the 13 golden cells, every T3/T4 snapshot queries persisted `SaleDelivery`, `BusinessSource`, `BusinessSourceLine`, loaded `TripManifestLine`, `DeliveryStockIssue`, `ReceivableOpenItem`, `CashReceipt`, `ReceiptAllocation`, `VerifiedCashEvidence`, `BCreditGrant`, `BGrantConsumption`, `AConfirmedLineSnapshot` and `AOrder`.

- Each sale must match **exact** loaded source right/line, SKU, quantity, payable amount and source kind; issues and AR items count and sum independently match sale records, physical stock depletion and cumulative source-backed COGS. `ReceivableOpenItem` outstanding must reconcile to **both** ledger ARs. Synthetic verified cash receipt/applied amount must equal custody, match a persisted independent witness and have one receipt allocation; grant-source consumption must equal grant's mutable consumed counter.
- B20 has precisely seven source-linked ledger effects and expected signs/amounts: physical −20/−200, COGS +200, gross AR +300, cash-allocated AR −200, custody +200, consumed B right −20 and consumed credit grant −100. A25 has precisely four: physical −25/−250, COGS +250, AR +375 and consumed A right −25. Unexpected, duplicated, omitted or altered effect keys/kinds/quantities/amounts fail even when a grand-total golden field happens to match.
- The A sale cannot create a receipt or spend B credit. B receipts, allocations, consumed credit and B count remain unchanged T3→T4; the rejected B31 never acquires a source even after later postings.
- Before/after T2 rejected command and T3/T4 idempotent replay, compare **all twelve numeric golden fields**, physical/manifest/source counters, full-trip ordered ledger SHA-256 (includes rights), and a digest of independently queried posting state. Two tenants are provisioned as separate nonce-owned and marked LocalDB databases; while one tenant advances, the other tenant's SQL snapshots and digests must remain unchanged.
- Mutation sentinels cover step drift, numeric drift, missing observed metrics and rights-ledger fingerprint changes even if golden numbers are unchanged. The broader C.2/C.3 transactional suites remain required for concurrent A/B races, SQL fault rollback and four-decimal moving-average edge cases.

## Reproducible evidence contract

On Windows / SQL Server LocalDB, from repo root:

```powershell
sqllocaldb start MSSQLLocalDB
./scripts/Verify-Business.ps1
python tools/validate_business.py
dotnet restore FmcgDms.slnx
dotnet build FmcgDms.slnx -c Release --no-restore
$env:APP00A_SQL_SERVER = '(localdb)\MSSQLLocalDB'
$env:APP01C4_TEST_RUN_ID = 'local-app01c4-<id>'
$env:APP01C4_SOURCE_HEAD_SHA = (git rev-parse HEAD)
dotnet test --solution FmcgDms.slnx -c Release --no-build --minimum-expected-tests 96 -- --report-trx --coverage --coverage-output-format cobertura
```

The test writes independent `TestResults/APP-01C4/tenant-a-oracle.json` and `tenant-b-oracle.json` in the test assembly output, retained in CI's `app00a-test-evidence` artifact. Report schema `APP01C4_SQL_ORACLE_V1` includes nonce DB, tenant, environment, TestRunId, exact PR source SHA (distinct from the synthetic workflow merge SHA), workflow SHA, locked oracle hash, all five SQL snapshots with posting and ledger fingerprints, **65 individual expected/actual/diff/match records**, and failure details. Reports are generated in `finally` on failure, with `FAIL_OR_INCOMPLETE` rather than a partial PASS. Transient TestResults must not be committed.

CI requires **at least 96 xUnit tests** (previous 93 + three C.4 cases), both APP-01C.4 65/65 actual/diff JSON files, both existing APP-01B.3 39/39 reports, exact run/head/hash identities, exactly five staged snapshots, 13 cells per row and source-linked 7+4 effects. Retain TRX/Cobertura, locked business audit, frontend npm audits/build and 100%-only Playwright/PWA. **125% remains Owner `EXCEPTION_ACCEPTED / NOT_TESTED`, not a CI gap or follow-up requirement.**

## Exit and handoff

For acceptance, independently review the SQL derivation (especially AR sign, credit consumption, witnessed cash, rights-only fingerprint, frozen A terms and tenant separation), resolve findings, rerun CI on the **final changed PR HEAD**, record source SHA, Actions run URL, TestRunId, both JSON artifact links and test counts, then merge. Only then may APP-01C be closed as bounded `SYNTHETIC_VERIFIED` through T4 with **130/130** persisted checks.

**Not authorized / not verified:** T5 cashier acceptance and T6 return (APP-01D), complete AC-03 **91/91 per tenant**, APP-01E wider race/recovery/reconciliation coverage, real B_ONLINE/cash verification/policy, legal invoices, EV/P0, pilot or tenant go-live. A green 65/65 model-only result is not production evidence.
