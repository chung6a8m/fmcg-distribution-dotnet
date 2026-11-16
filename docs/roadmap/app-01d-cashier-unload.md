# APP-01D — T5 cashier acceptance and T6 vehicle unload / remainder return

**State: MERGED / SYNTHETIC_VERIFIED on [PR #32](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/32).** Independently reviewed source HEAD `d32cd507`, [Actions #43878243524 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43878243524) SUCCESS: 99/99 tests, 91/91 SQL cells per tenant, `app01d-43878243524-2`. The review fixed partial-B-load release-only and B allocation pool conservation; full evidence on PR. ** Synthetic, nonproduction, `SYNTHETIC_UNITLESS`, `invoiceEligible=false`. Business Baseline and `business.lock.json` remain immutable. This PR does **not** authorize production cash, legal invoicing, accounting, EV/P0 or tenant go-live.

## Contract and SQL source of truth

Source: locked `business/reference-data/expected-results/golden-oracles.json#AC-03` (all **7 checkpoints x 13 named cells = 91/91 per tenant**) and `business/workflows/operations.md` SOP-05/SOP-08, BR-04/07/11/13/14 and AC-03. Previous C.4 independently verified five checkpoints: T0–T4.

- **OFFER**: sales actor submits `OfferCashHandover` for one `Trip` and amount; source and `CashHandoverOffer` are immutable; **zero** ledger/custody/fund/AR effects. Offer amount cannot exceed source-reconciled unhanded customer cash. A replay is idempotent; duplicate SourceNumber cannot create a second offer.
- **CASHIER_ACCEPTED**: separate authenticated synthetic `cashier` actor, **independently seeded** `CashierCountEvidence` with exact tenant/Trip/amount and `CountedBy`, must differ from offer's actor. Only the acceptance transaction creates `CashHandoverAcceptance` and exactly two source-line ledger legs `CUSTODY-OUT -200` and `FUND-IN +200`. One acceptance per offer and per count evidence; does not touch receipt allocation or AR. Missing/incorrect count, same actor, already accepted and depleted custody produce durable rejection without money effects. Nothing trusted solely from client flags.
- **UNLOAD**: warehouse actor provides the **preloaded manifest and Trip**; service re-derives remaining quantity from immutable load transfer less linked delivery issues for **every manifest line**. Consistency of A/B rights with posting quantities and source-derived physical stock and cost is checked under the same Trip app-lock as B20/A25. One terminal `TripUnload` with one immutable source line per positive physical remainder or B-only quota release (zero quantity/cost). When a B grant is loaded only partially, T6 releases the entire unused quota (including the never-loaded part) independently of physical stock, and atomically returns the released capacity to `StockPool.AllocatedBaseQuantity`; A holds remain unchanged. Atomic paired `PHYSICAL_STOCK` legs return A15/150 + B10/100 vehicle→warehouse; B unused quota10 is released with a separate `RIGHT_B_RELEASED -10` leg. A residual reservation15 remains **HELD**; no AR/COGS reversal or customer return/QA processing. Trip moves to `RECONCILING`, not silently `CLOSED`.
- Commands share APP-01A's `(TenantId,ProducerId,CommandId)`, canonical SHA-256 hash v1, `SERIALIZABLE` transaction and source identities. Cashier is a separate synthetic JWT role. Backend routes only exist in Development/IntegrationTest with synthetic security enabled and nonce-owned disposable SQL Server LocalDB tenants.

Migration: `App01dCashierUnloadSchema` version `202611160008`. Separate append-only evidence tables with tenant/Trip/source/manifest FKs and SQL immutability triggers. Cash OFFER ledger is prohibited by an **INSERT trigger**, not just handler code.

## Expected staged SQL balances

| Field | T4 A25 | T5 cashier ACCEPT | T6 unload |
|---|---:|---:|---:|
| MAIN quantity / cost | 30 / 300 | 30 / 300 | **55 / 550** |
| TRIP quantity / cost | 25 / 250 | 25 / 250 | **0 / 0** |
| Sold quantity / COGS | 45 / 450 | 45 / 450 | 45 / 450 |
| A held / B quota | 15 / 10 | 15 / 10 | **15 / 0** |
| AR A / AR B | 375 / 100 | 375 / 100 | 375 / 100 |
| Cash custody / fund | 200 / 0 | **0 / 200** | 0 / 200 |

Only locked oracle values may be used for comparator expected cells. **Do not** backcompute expected values from service response or implementation. `Capture` reads actual directly from SQL balances/stock rights and source-backed ledger. Comparator checks one-and-only-one of each field, exact decimal actual and signed diff for both tenants, with baseline bytes SHA-256, nonce database, source HEAD, workflow SHA and `TestRunId`.

## Acceptance and negative checks

1. Fresh/repeated schema migration on **two separate nonce-owned LocalDB tenant DBs**. New FKs, immutable evidence; no SQL side-table lacking tenant enforcement.
2. T0→T6 each tenant: **91/91 SQL actual/diff**, seven reports/checkpoints with correct custody/fund source identity, 2 acceptance effects + 5 unload effects, full-trip ledger hash and unchanged commercial posting digest.
3. Offer non-posting; absent/wrong cashier witness rejected; sales role denied from cashier endpoint; replay/changed payload conflict, double accept and double unload rejected without effects.
4. Source lineage: original loaded 70, deliveries B20/A25, unload A15/B10, released B10 and retained A15, full physical conservation (100 quantity / 1000 cost), frozen A price/N, AR_A375/AR_B100 and COGS450 unchanged.
5. Per-tenant isolation and APP-01B.3 **39/39**, APP-01C.4 **65/65** regression; MTP test floor **99**, Windows LocalDB build, TRX/Cobertura, immutable business lock, frontend/PWA/Playwright 100% only.
6. Independent review on exact final source PR HEAD, verified GitHub CI artifacts and no unaddressed findings before merge. Any changed HEAD requires renewed CI.

### Scope restrictions

These are **synthetic fleet remainder** movements, **not** customer return requests, quarantine/QA, credits/refunds, period-locked cost adjustments or EV-05. APP-01E will cover stronger two-client races, lost ACK/restart, cross-module retry/rollback and reconciliation. Legacy/production writer cutover, seller/channel, B real/offline grant and tax/accounting remain NOT_VERIFIED / NOT_AUTHORIZED. Owner's 125% browser scale exception remains ACCEPTED / NOT_TESTED.

## E.1 handoff

APP-01D is bounded and verified for T0–T6 synthetic state only. [APP-01E.1](app-01e1-recovery-harness.md) adds two-client admission and test-only pre-/post-COMMIT recovery probes. This does not retroactively verify production lost ACK, process restart or full AC-11.
