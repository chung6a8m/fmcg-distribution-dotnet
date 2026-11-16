# APP-01B proposal — T0→T2 manifest, custody transfer and quota rejection

**State (2026-11-16): APP-01B.1 and APP-01B.2 MERGED; APP-01B.3 SQL_ORACLE_VERIFIED, with final reviewed-HEAD CI and merge recorded in [PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27).** This executable synthetic/nonproduction proposal is now paired with the [APP-01B closeout](../closeout/2026-11-16-app-01b.md). Owner authorization remains restricted to the APP-01A..E synthetic roadmap; it does not authorize real warehouse custody, full 91/91, production/pilot, real customer credit or invoices.

## Business contract and oracle

Sources (immutable): [AC-03 expected steps](../../business/reference-data/expected-results/golden-oracles.json), [acceptance](../../business/acceptance.md), [module A workflow](../../business/workflows/module-a.md), [B_ONLINE workflow](../../business/workflows/module-b-online.md). Use `SYNTHETIC_UNITLESS` and `invoiceEligible=false`; never change business oracle to fit the implementation.

| Checkpoint | MAIN Qty / Cost | TRIP Qty / Cost | A held / B quota | Sold / COGS / AR_A / AR_B / Custody / Fund |
|---|---|---|---|---|
| T0 | 100 / 1000 | 0 / 0 | 40 / 30 | all zero |
| T1_LOAD | 30 / 300 | 70 / 700 | 40 / 30 | all zero |
| T2_REJECT | 30 / 300 | 70 / 700 | 40 / 30 | all zero |

**T1:** single tenant/Trip manifest containing A40 and B30 moves 70 units at synthetic unit cost 10 from MAIN stock location to vehicle TRIP. It is a **stock custody transfer**, not sale; no AR, COGS, revenue, receipt or fund. A/B rights remain distinct, associated with eligible source/Trip/lines.

**T2:** attempted B31 at price15 (offered/verified synthetic cash 465) exceeds remaining B quota30, returns `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT`, and commits **no sale, receipt, custody, stock, cost, AR, invoice or grant-consumption effect**. Never post offered cash as received after rejection.

## Engineering scope — recommended small PR split

1. **APP-01B.1 — schema and state modeling:** propose `TripManifest/Line`, source-backed stock movement and per-location on-hand/cost balances (or equivalent) with tenant/Trip/source binding, unique movement identity and decimal exactness. Distinguish opening stock snapshot, allocated entitlement `StockPool/StockRight`, and **physical warehouse/vehicle stock balances**. Fresh and repeat migration in two nonce-owned LocalDB DBs. Do not treat `StockPool.OnHandBaseQuantity` as if it is a mutable physical ledger.
2. **APP-01B.2 — transactional manifest load and B31 preflight:** Dapper explicit Serializable transaction, same command/source hash/version and actor/tenant guards as APP-01A. Lock appropriate pool, Trip/manifest and physical balances, atomically move quantity and cost while preserving A/B rights; reject over-load, wrong Trip/warehouse/SKU, same source across modes, repeated or mixed-payload command. Introduce only the minimal **synthetic preflight** B31 validation needed for T2; **do not implement/post full B sale/receipt** ahead of APP-01C.
3. **APP-01B.3 — executable T0–T2 evidence:** persist synthetic opening and compare **every oracle field** in T0/T1/T2 (3 steps × 13 fields = **39/39 target**, including step identities) with app-persistence actual/diff. Two isolated tenant DBs; replay, concurrency (two loads cannot steal stock/right), injected SQL fault after movement with full rollback and same-key retry, overquota B31 zero-effect, actor/tenant negatives and no AR/COGS on load. Record `TestRunId`, source HEAD SHA, exact CI run, commands, fixtures and artifact links in PR.

## Exit gate and exclusions

- No duplication or overlap of A/B rights; physical stock/cost conserved at load; manifest matches rights and Trip; no stock below zero or double transfer under concurrent/racing commands.
- T0/T1/T2 persisted actual values **exactly match immutable 39/39 oracle cells**; `T2_REJECT` leaves all T1 balances unchanged, with no receipt/cash creation. Test explicit `REJECTED_QUOTA` / `B_QUOTA_INSUFFICIENT`.
- Verify Business Baseline lock, no production DB, two LocalDB migration replay/tenant cross-read-write negatives, .NET/Playwright/PWA gates, independent review and final-HEAD CI after fixes. Only then mark **APP-01B SYNTHETIC_VERIFIED**.
- **NOT INCLUDED:** sale/delivery actual posting (APP-01C), AR/COGS/cash/credit consumption or receipt, cashier handover/return (APP-01D), full 91/91 and recovery cross-module matrix (APP-01E), real price/tax/legal invoice, B_ONLINE external mode, offline business writes, EV/P0 or tenant go-live.

**Closeout and next increment:** [APP-01B.1 PR #25](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/25) establishes physical schema and [APP-01B.2 PR #26](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/26) implements manifest and B31 Dapper commands. [APP-01B.3 PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27) verifies **39/39 SQL-backed T0–T2 oracle cells independently for both LocalDB tenants (78/78 total)** and hardens T2 with the full-trip ledger fingerprint and rejected-command source absence. Final evidence/review/merge are linked from PR #27; see [closeout](../closeout/2026-11-16-app-01b.md). Next is APP-01C T3/T4 source/ledger posting with separate test and approval gates.
