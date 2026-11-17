# APP-01C.3 — Atomic T4 A25 partial delivery with frozen A confirmation

**Status (2026-11-17): MERGED / COMMANDS_VERIFIED** ([APP-01C.3 PR #30](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/30), merged as main `81eecf6cca46f1b34f9f9e01fe4c47ca56309c67`). Its later [APP-01C.4 PR #31](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/31) independently verified the T0–T4 SQL oracle (**65/65 per physical LocalDB tenant**) on [Windows CI #43856950012](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43856950012), **96/96 tests**, followed by APP-01D/E synthetic T0–T6 closeout. Synthetic-only two-tenant nonce-owned SQL Server LocalDB; not a production transaction, invoice, tax/revenue journal, actual customer approval or go-live authorization. This stage follows merged [APP-01C.2 B20](app-01c2-b20-atomic-posting.md) and the source-bound [C.1 schema](app-01c1-posting-schema.md). Immutable [Business Baseline AC-03](../../business/reference-data/expected-results/golden-oracles.json) remains locked.

## Confirm snapshot is the contract

- The existing `ConfirmAOrder` command persists **one immutable AConfirmedLineSnapshot per confirmed AOrderLine** inside its original Dapper `Serializable` transaction: original confirmed quantity, price, exactly one Confirm source/line, synthetic approved N-day terms, and timestamp. No AR, revenue, COGS, stock issue, custody or receipt is posted on Confirm.
- Migration `202611160007` creates `AOrderTermsApproval` as a **separately witnessed** synthetic policy record. Approval must be `APPROVED`, valid/current, maker/checker-separated, evidenced, with explicit `TermsDays` between 1 and 365, not merely a client-provided `approved` flag. A test fixture may seed a seven-day approval; real policy remains UNSET. Approved approval rows and confirmed snapshots are append-only/immutable. A missing, pending, expired, self-checked or nonmatching witness rejects confirmation with `A_TERMS_APPROVAL_REQUIRED`.
- `AOrderLine.UnitPrice` and mutable customer/policy defaults are **not** A25 pricing authority. If legacy A orders have no frozen AConfirmedLineSnapshot, A25 is rejected; it never silently backfills or guesses historical terms. A25 command accepts **no** input price or due date.

## A25 command and atomic effects

`POST /api/synthetic/app01c/commands/post-a-partial-delivery`, `SyntheticSales` role with signed actor and server-routed tenant, only when synthetic auth is explicitly enabled in Development/IntegrationTest and database passes nonce-owner/tenant binding. The command references exact Trip, Customer, ShipTo, AOrderId, AOrderLineId, **loaded** A TripManifestLineId, SKU and base quantity; `ProducerId`, `CommandId`, `SourceNumber` share the APP-01A canonical idempotency namespace.

1. Begin one explicit SQL Server `Serializable` transaction and canonical command identity app-lock. Same-key + same semantic payload replays; changed payload/cross-kind ID yields HTTP 409 CONFLICT. Acquire the **same per-tenant/Trip app-lock as B20** to serialize A/A and A/B vehicle races.
2. Lock and validate Trip, active Customer/ShipTo/SKU, exact loaded A right→reservation→order line→immutable Confirm snapshot, independently approved terms and confirmation status. Check cumulative delivered vs remaining manifest, A right, A line and whole confirmed order. Reconcile vehicle physical quantity/cost against immutable opening + loaded movements − posted issues. Fail closed with durable no-business-effect rejection on missing/short/drifted provenance.
3. Calculate `payable = deliveredBaseQuantity × frozenUnitPrice` in exact decimal (no client price). `dueBusinessDate = Trip.BusinessDate + frozenTermsDays`. Determine moving-average source custody cost to 4 decimals, preserving residual on full depletion.
4. In **the same transaction**, persist `BusinessSource(A_DELIVERY)`+line, increment only the source A right consumed quantity, debit physical vehicle qty/cost, append `SaleDelivery(A)` and immutable `DeliveryStockIssue`, create one due-dated `ReceivableOpenItem(A)` with `Applied=0`, append exactly 4 source-line `LedgerEffect` rows (`PHYSICAL_STOCK`, `COGS`, `AR`, `RIGHT_A_CONSUMED`), and set AOrder `PARTIALLY_DELIVERED` or `FULFILLED` based on actual cumulative delivery. Assert issue/AR/effect cardinalities and zero cash receipt/B grant consumption for A.
5. Complete command and commit once. Any injected SQL fault (including after effects) rolls **all** source, stock, rights, order status, AR, ledger and command identity back; repair then same-key retry succeeds. Duplicate source, altered payload and competing allocations cannot double-post.

**No B rights, B grant credit, verified cash receipt, driver custody, cashier fund, invoice, tax, or revenue booking** changes on A25. No processing of returns, partial closes or cash-on-delivery A policy in this synthetic increment.

## T4 expected, after T3 B20

| Persisted source-backed metric | T3 | T4 A25 |
|---|---:|---:|
| Warehouse physical quantity / cost | 30 / 300 | 30 / 300 |
| Vehicle physical quantity / cost | 50 / 500 | **25 / 250** |
| Total actually sold quantity | 20 | **45** |
| Cumulative COGS | 200 | **450** |
| A reservation remaining | 40 | **15** |
| B quota remaining | 10 | **10** |
| AR_A / AR_B | 0 / 100 | **375 / 100** |
| Driver cash custody / cashier fund | 200 / 0 | **200 / 0** |

Unit price frozen at 15, `25 × 15 = 375`; physical issue cost `25 × 10 = 250`. Split A delivery `25+15` creates **two distinct** AR open items and their own independent due dates, and only the second fully fulfills the order. A new, separately confirmed order may use newer terms without changing old debt.

## Verification and handoff

- New `App01c3APartialDeliveryTests` (11 real LocalDB xUnit cases) cover both-tenant T4 after B20, split deliveries and full fulfillment, frozen price/terms after mutable master edits, no/pending approval, canonical replay/conflict, foreign tenant/actor/wrong source, concurrent A/A and A/B posts, short/inflated/mispriced vehicle, injected after-ledger rollback/retry, and four-decimal moving-average cost.
- Updated existing A confirm fixtures to seed separately witnessed A terms. Prior APP-01B.3 independent SQL 39/39 actual/diff report **per tenant** remains a CI gate. Audit-as-of also checks A source/AR/stock/due/4 ledger legs and orphan consumed rights; a green HTTP result alone is insufficient.
- CI minimum rises **82→93 xUnit tests**, Windows LocalDB, .NET SDK 10.0.404, Node 26, xUnit v3/MTP; `TestRunId=app01c3-<run>-<attempt>` records exact PR source HEAD SHA separately from synthetic merge SHA. Retain TRX/Cobertura, Business Baseline hash/immutability, frontend/npm checks and 100%-only Playwright/PWA. **125% display remains Owner-accepted NOT_TESTED**.
- **Historical C.3 review gate (completed):** reviewer checks included source FK/approval immutability, SQL rights/physical stock aggregates, A/B lock ordering, audit-as-of, due dates, idempotency and rollback. APP-01C.3 was independently reviewed and merged in [PR #30](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/30); the canonical [APP-00–APP-01 roadmap](app-00-app-01.md) records its COMMANDS_VERIFIED closeout. The 93-test floor describes the original C.3 acceptance target, not a current pending CI run.
- **Subsequent verification completed:** APP-01C.4 independently derived T0–T4 from persisted SQL, **65/65 per tenant** (130/130 across two physical tenant databases), with actual/diff, ledger fingerprint, mutation sentinels and fail-closed CI provenance ([PR #31](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/31)). APP-01D later verified **91/91 T0–T6 per tenant** and APP-01E.5 closed the wider synthetic reconciliation matrix. This historical C.3 design does **not** confer production B_ONLINE/real approval, invoices, EV/P0, pilot or go-live authorization.
