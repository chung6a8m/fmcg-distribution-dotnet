# APP-01B proposal — T0→T2 manifest, custody transfer and quota rejection

**State: APP-01B.1 SCHEMA_VERIFIED; APP-01B.2 MERGED / CI VERIFIED (PR #26); APP-01B.3 SQL ORACLE TEST + CI GATE IMPLEMENTED / EXACT-HEAD VERIFICATION PENDING (2026-11-16).** Owner authorized the APP-01A..E *synthetic nonproduction* roadmap in [owner decisions](../decisions/owner-app-2026-11-15.md). APP-01A [closeout](../closeout/2026-11-16-app-01a.md) is SYNTHETIC_VERIFIED. This document proposes the next small vertical slice; it is **not** a claim of APP-01B acceptance, 39/39 golden verification or real warehouse custody.

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

**Current increment:** [APP-01B.1 physical schema and LocalDB test contract](app-01b1-physical-stock-schema.md) is SCHEMA_VERIFIED via [PR #25](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/25) with independent review and final-HEAD CI. APP-01B.2 [implements](app-01b2-manifest-commands.md) the synthetic LoadManifest and B31 preflight with physical LocalDB tests; independent review and exact PR-head CI evidence are tracked in PR #26. The [APP-01B.3 SQL-backed oracle test](app-01b3-sql-oracle.md) compares T0–T2 against app persistence in **two** tenant LocalDBs (39 cells per tenant) and produces per-cell actual/diff artifacts. Acceptance requires independent review and exact-HEAD CI; until that evidence is recorded, **39/39 remains NOT_VERIFIED**.
