# APP-01C.2 — Atomic B20 sale, delivery, verified receipt and ledger

**Scope/status (2026-11-16): IMPLEMENTED / CI_AND_INDEPENDENT_REVIEW_PENDING.**
Synthetic LocalDB only. This increment adds a **B20 transactional posting** on top of
[APP-01C.1 source/ledger contract](app-01c1-posting-schema.md). Final evidence,
review findings and approval must be attached to the PR before marking verified.
[Roadmap](app-00-app-01.md) · [immutable AC-03 oracle](../../business/reference-data/expected-results/golden-oracles.json).

## Authority and conditions

- The only operation is `PostBSaleDeliveryReceipt` under
  `POST /api/synthetic/app01c/commands/post-b-sale-delivery-receipt`.
  The route exists only in IntegrationTest/Development with synthetic JWT enabled,
  a `SyntheticSales` role, server-resolved tenant, and nonce-owned marked LocalDB.
  No caller-provided tenant connection string, manual `VERIFIED` flag or offline write.
- The command names the exact **loaded B manifest line**, Trip, SKU, Customer and
  ShipTo, validated against composite FK/source lineage, active entities, exact
  stock right, B quota and outward vehicle movement. The actor is a signed `sub`
  equal to `actor_id`; no warehouse-only or foreign-tenant posting.
- Sale quantity must fit *both* remaining B right and this manifest line's
  loaded quantity minus prior `SaleDelivery` rows. Physical vehicle on-hand and
  cost must be available. Unit price and received cash are exact decimals,
  `0 < verified cash <= quantity × price`; no discount, tax or legal invoice.
- **Cash evidence is not client authority.** Migration `202611160006` adds
  `VerifiedCashEvidence`: a preexisting witnessed `VERIFIED` physical cash
  record (independently seeded by test harness only) for the exact tenant,
  Trip/Customer/ShipTo, cash amount, non-sales verifier and evidence reference.
  A composite FK and receipt evidence uniqueness require this persisted witness
  and reject pending/mismatched/reused references. The witness is immutable once
  verified. No real-world cash-verifier API is implemented or authorized.
- Credit exposure = payable − verified receipt application. Only actually
  granted B credit in the same Trip/Customer/ShipTo, with synthetic approved
  source evidence, can fund that residual. Multiple distinct grants are spent
  in deterministic ID order and each consumption has an immutable source row.
  Grant/stock-right mutable counters are checked against their source history.

## Atomic command envelope

One explicit **Dapper SQL `Serializable` transaction** performs:

1. APP-01A canonical command identity app-lock, payload hash, persisted replay
   or changed-payload `CONFLICT`. A per-tenant/Trip transaction app-lock
   serializes rival B20 posts; A25 must use this lock too.
2. Trip, loaded manifest, exact right and physical source preflight under
   deterministic locks; cash witness and credit/grant validation. A business
   rejection preserves only `BusinessCommand(REJECTED)`, with **no** sale,
   delivery, receipt, rights, physical or ledger effect.
3. Create `BusinessSource(B_SALE_DELIVERY)` + one `BusinessSourceLine`
   with exact quantity/payable and source actor; increment consumed B right.
4. Move vehicle `PhysicalStockBalance` down by source quantity and
   **moving-average four-decimal cost**, then write `SaleDelivery` and immutable
   `DeliveryStockIssue`, `ReceivableOpenItem`, witnessed `CashReceipt` and
   `ReceiptAllocation`, and increment bounded AR/cash applied counters.
5. Debit only the *residual* B credit via `BCreditGrant`, append one
   `BGrantConsumption` per grant and seven source-line ledger effects for
   partial cash: PHYSICAL_STOCK, COGS, AR gross, AR cash allocation, CUSTODY,
   RIGHT_B_CONSUMED and RIGHT_CREDIT_B_CONSUMED. No FUND/REVENUE/A-right effect.
   Full cash has six effects and no grant debit.
6. Assert SQL issue/allocation/receipt/AR/credit/effect cardinalities and sums,
   finalize command and commit together. Any SQL exception rolls back every
   prior write, including command row, allowing same-key retry after repair.

The source/ledger accounting model does **not** post production financial
journals, tax or revenue. The custodian holds received cash until a distinct
cashier-accept operation in APP-01D; fund remains zero. APP-01A initial
allocation ledgers remain unchanged: consumption has distinct negative
`RIGHT_B_CONSUMED`/`RIGHT_CREDIT_B_CONSUMED` source-bound kinds.

## AC-03 T3 example after T2

| Persisted field | T2 | T3 B20 |
|---|---:|---:|
| Main stock quantity / cost | 30 / 300 | 30 / 300 |
| Vehicle stock quantity / cost | 70 / 700 | **50 / 500** |
| Sold quantity | 0 | **20** |
| Cumulative COGS | 0 | **200** |
| A reserved remaining | 40 | **40** |
| B quota remaining | 30 | **10** |
| AR_A / AR_B | 0 / 0 | **0 / 100** |
| Driver cash custody / fund | 0 / 0 | **200 / 0** |

B20 payable is 20×15=300; receipt verified and allocated is 200; remaining
AR_B=100 equals B grant consumption100. The T2 B31/15/465 rejection remains
persisted as a no-effect command and must stay unchanged.

## Verification and limits

- New `App01c2BPostingTests` (10 xUnit tests; LocalDB) cover two tenant
  fixtures: T3 exact SQL balances/source cardinality and ledger, identical
  replay/canonical decimal/cross-kind conflict, pending/wrong/fake/self-witness,
  quota and credit insufficiency, short vehicle stock, two concurrent B sales
  racing for right and grant, SQL-triggered rollback after receipt allocation
  with same-key retry, wrong actor/cross-tenant, multiple source grants, and
  fractional moving-average exactness. Prior APP-01B.3 39/39 T0–T2 SQL
  oracle per tenant remains a separate independent gate.
- CI target **82/82** total minimum (72 baseline + 10 B20), pinned
  SDK 10.0.404, Windows LocalDB, `TestRunId=app01c2-<run>-<attempt>`,
  exact PR source HEAD vs synthetic merge SHA, TRX/Cobertura, locked business
  baseline, npm security, frontend and 100% Playwright/PWA.
- **Not claimed until independently reviewed green final HEAD:** B20
  `COMMANDS_VERIFIED`, APP-01C.4 T0–T4 **65/65**, full AC-03 **91/91**,
  synthetic cash verification process, production B_ONLINE, real credit policy,
  invoice, A25, A/B concurrent mixing, EV/P0, pilot or go-live.
  125% UI scaling remains `EXCEPTION_ACCEPTED / NOT_TESTED`.

## Handoff

APP-01C.3 will freeze A confirmed unit price and N-day terms explicitly
before A25, use the same per-Trip lock and physical cost reconciliation, and
post A rights/AR/COGS without B grant/receipt/custody effects.
APP-01C.4 must derive the full 13-field T0–T4 oracle directly from persisted
tables/effects and independently reconcile posted B20 plus A25 lineage.
