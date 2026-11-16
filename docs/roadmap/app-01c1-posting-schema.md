# APP-01C.1 — Synthetic T3/T4 posting schema and source/ledger contract

**Status: SCHEMA_IMPLEMENTED / REVIEW_AND_CI_PENDING.** This document defines the nonproduction, unitless, no-invoice schema stage only. No B20 or A25 command is implemented in this increment. See [roadmap](app-00-app-01.md) and [immutable AC-03](../../business/reference-data/expected-results/golden-oracles.json).

## Source authority and handoff

- **Starting point:** APP-01B T0–T2 (PRs #25–#27), nonce-marked two LocalDB tenants, physical MAIN 100/1000, TRIP 0/0, manifest A40/B30 loaded 70/700; durable B31 REJECTED_QUOTA, **39/39 per tenant** from persistence.
- **Business Baseline:** BR-03/04/07/08/10/11/12/13/16 and AC-03. Do not change business/ or business.lock.json. Use SYNTHETIC_UNITLESS and invoiceEligible=false.
- **APP-01C.2 T3 B20:** B20 × synthetic unit price 15 = payable 300, verified cash R=200, AR_B=100, delivery stock issue 20/200, B quota remaining 10, B credit consumption 100 from approved/granted synthetic credit, custody +200, fund 0.
- **APP-01C.3 T4 A25:** delivery 25 × confirmed price 15 = AR_A=375, issue 25/250, A hold remaining 15, no receipt/custody/fund. T3→T4 vehicle 50/500→25/250; cumulative COGS 200→450.
- **APP-01C.4 SQL oracle:** exactly T0..T4 = 5 × 13 = **65/65 per tenant**, 130/130 combined. Full **91/91** requires APP-01D T5/T6.

## Migration 202611160005 (schema only)

App01c1PostingSchema.cs adds seven source-bound tables and immutable posting evidence:

| Table | Source and physical constraints |
|---|---|
| **AConfirmedLineSnapshot** | A confirmed price and N-day terms; immutable; exact AOrderLine, tenant/Trip/Customer/SKU/qty and A_CONFIRM source line |
| **SaleDelivery** | One committed A delivery or B sale+delivery per source line; loaded manifest, A/B right, Customer/ShipTo, Trip, SKU, source qty/payable, immutable |
| **DeliveryStockIssue** | One outward physical stock issue per delivery/source line; exact qty/cost/SKU through composite FK, append-only |
| **ReceivableOpenItem** | Gross payable plus applied amount, derived outstanding = original − applied; source and A due date immutable |
| **CashReceipt** | B-only verified physical CASH with immutable evidence; receipt received/applied counters with bounded checks |
| **ReceiptAllocation** | Verified receipt allocated to Customer AR item; same-customer composite FKs, immutable and unique receipt–item pair |
| **BGrantConsumption** | B-only matching Trip/Customer/grant consumption per sale, append-only; A cannot consume B grant |

Composite FKs and alternate keys reject wrong tenant/Trip/Customer/SKU, wrong A/B right, wrong source kind, mismatched source quantity/amount, and a draft/un-loaded manifest source line. Posted-delivery ledger effects cannot be rewritten through TR_LedgerEffect_PostedDeliveryImmutable. Existing APP-01B transfer, rights, hash, SQL oracle and physical opening remain untouched.

**Boundary:** SQL FKs and CHECK constraints alone cannot guarantee SUM(delivered quantity) ≤ loaded/available right, SUM(consumed credit) ≤ grant, SUM(allocations) ≤ verified receipt or physical/COGS reconciliation across multiple records. C.2/C.3 must check these aggregates under deterministic locks in the same explicit Serializable transaction. Schema tests are not posting correctness.

## Next transactional contracts (NOT_IMPLEMENTED here)

### APP-01C.2 — PostBSaleDeliveryReceipt

One Dapper **explicit Serializable** transaction using signed synthetic server tenant and actor, canonical APP-01A command identity (tenant, producer, command), safe replay/changed-payload CONFLICT. Lock command, Trip/manifest/right, vehicle balance, grant and receipt/AR counters in deterministic order. Reject before posting for missing or unverified cash, insufficient quota/credit, wrong source/Trip/customer/role. T2 B31 remains non-posting.

A successful B20 atomically writes BusinessCommand and B_SALE_DELIVERY source/line, SaleDelivery, DeliveryStockIssue, ReceivableOpenItem (Original=300, Applied=200, Outstanding=100), CashReceipt verified 200, ReceiptAllocation 200, BGrantConsumption 100, StockRight consumption 20, BCreditGrant consumption 100, vehicle stock/cost −20/−200 and the complete source-linked ledger. Intended net effects: AR_B +300/−200 = +100, COGS +200, CUSTODY +200, RIGHT_B −20, RIGHT_CREDIT_B −100, PHYSICAL_STOCK −20/−200. No FUND, legal invoice, tax or production authorization.

Grant/rights counters and ledger must reconcile; APP-01A.3 audit currently treats grant/right ledger as initial amounts, so C.2 must extend reconciliation to consumption rather than silently weakening it. Required tests: two-client oversell and competing grant, after-effect fault rollback/retry, idempotency replay and conflict, fake receipt, source/role/tenant negatives, and zero A-pool consumption.

### APP-01C.3 — PostAPartialDelivery

An A order needs source-bound immutable confirmed price and terms. Existing AOrderLine.UnitPrice is mutable and ConfirmAOrder does **not** populate AConfirmedLineSnapshot. C.3 must extend confirm transaction/contract to write the immutable snapshot, require an explicit synthetic approved N-day term (never invent N=0), and reject older confirmed orders lacking immutable terms. Do not silently backfill an unknown policy. A25 uses confirmed price and dueBusinessDate = delivery date + frozen N, creates SaleDelivery(A), stock issue, AR_A +375, RIGHT_A −25, vehicle −25/−250 and COGS +250 in one transaction, with no B grant, custody, receipt or fund.

Tests must cover partial and split delivery, post-confirm master changes, overdeliver and cross-source, concurrent A/B consumption of same vehicle, role and tenant negatives, rollback/retry and no effect duplication.

### APP-01C.4 — Independent persistence oracle

Read all 13 fields directly from SQL balances/openings, stock rights, source/effects, sale/stock issue, receipt, gross/allocated AR and cash custody. Compare T0–T4 65/65 **per tenant**, independently, not pooled. Verify T3/T4 full source/ledger fingerprints, identity and cardinality, no hidden right/credit effects, and cross-tenant unchanged. Include mutation sentinels, baseline SHA-256, actual/diff JSON per tenant, TestRunId, final source HEAD separated from synthetic workflow SHA, fail-closed CI. Full 91/91 remains NOT_RUN until T5/T6.

## APP-01C.1 verification gate

App01c1PostingSchemaTests contributes **eight** LocalDB schema tests for two tenant migration/replay, A Confirm snapshot immutability, A/B source-anchored delivery, exact stock issue uniqueness/cost, receipt/AR bounded counters, B grant relationship, posted ledger immutability and negative cross-tenant writes. The tests insert synthetic SQL rows directly; **they do not execute T3/T4 commands**.

- On Windows with SQL Server LocalDB, .NET SDK 10.0.404, Node 26 LTS: run dotnet test --solution FmcgDms.slnx -c Release --no-build --minimum-expected-tests 72 -- --report-trx --coverage --coverage-output-format cobertura.
- Preserve independent APP-01B.3 39/39 tenant-A and tenant-B SQL actual/diff reports, immutable Business Baseline validation, TRX/Cobertura, Playwright/PWA at 100%. The 125% Owner exception stays NOT_TESTED.
- Independent review, resolve findings, rerun CI on final PR HEAD, record exact source HEAD SHA, TestRunId and artifacts before merge. After this stage, only **SCHEMA_VERIFIED** is possible. B20/T3, A25/T4 and 65/65 are **NOT_VERIFIED**.

No B_ONLINE production, legal invoice, external EV/P0, accounting reconciliation, real cash handling, pilot or tenant go-live authority is claimed.
