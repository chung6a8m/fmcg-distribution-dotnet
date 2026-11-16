# APP-01B.1 — Schema & Physical Stock State

**Status:** IMPLEMENTED ON FEATURE BRANCH / REVIEW AND CI PENDING (2026-11-16).  
**Scope:** synthetic, nonproduction, physical SQL Server LocalDB schema **only**. No T1 load command, B31 rejection command, T0–T2 oracle run, 39/39 acceptance, or production authorization.

Source: [APP-01B proposal](app-01b-manifest-transfer-proposal.md), [immutable AC-03 oracle](../../business/reference-data/expected-results/golden-oracles.json), [APP-01A closeout](../closeout/2026-11-16-app-01a.md). `business/` and `business.lock.json` are unchanged.

## Physical state versus allocation rights

| Persisted resource | Meaning | APP-01B.1 responsibilities |
|---|---|---|
| `StockPool` / `StockRight` (APP-01A) | Synthetic opening entitlement and A/B allocation, **not** physical location custody | Left unchanged; stock rights remain source-/Trip-/SKU-bound |
| `PhysicalStockOpening` | Explicit **immutable** T0 quantity and cost snapshot for one tenant/location/SKU | Composite tenant/location/SKU PK and FKs; nonnegative decimal(18,3)/decimal(19,4); zero quantity implies zero cost; snapshot is not auto-seeded from `StockPool` |
| `PhysicalStockBalance` | Mutable physical on-hand quantity and carried cost at warehouse or vehicle | Composite PK and FK to explicitly seeded opening; guards against negative quantities/costs and orphan positive cost when quantity is zero; `rowversion` for concurrency |
| `TripManifest` | One manifest per Trip, warehouse → its actual vehicle | FK Trip + vehicle identity, warehouse/vehicle kind checks, distinct locations, `DRAFT` versus `LOADED`, optional source until loaded, exact `MANIFEST_LOAD` source kind and Trip lineage |
| `TripManifestLine` | One scoped A or B right staged for a manifest | Exact composite FK to `StockRight` Trip/warehouse/SKU/kind; unique right prevents second manifest use; positive planned quantity; loaded quantity limited to planned; loaded cost nonnegative; exact source-line identity, line number, SKU, quantity, cost |
| `PhysicalStockMovement` | **One paired transfer record per loaded line**, from manifest warehouse to vehicle | Unique manifest line and source line; FK binds movement quantity/cost to the loaded line and its source; the two locations come from the manifest (not user-supplied movement legs); immutable after insert |

Migration: `202611160004`, class `App01b1PhysicalStock`. All tables carry `TenantId` and rowversion. Composite alternate keys on existing `StockLocation`, `Trip`, `BusinessSource`, `BusinessSourceLine` and `StockRight` make cross-scope references fail by FK; no existing APP-01A data or API contract is rewritten.

**Important:** The physical movement record is append-only evidence, **not a balance-changing trigger**. It does not by itself prove balanced custody. APP-01B.2 must atomically lock/validate physical balances, right availability, manifest, source and command, update both warehouse/vehicle quantity and cost, create source/effect/movement, and transition `DRAFT → LOADED` in **one SERIALIZABLE Dapper transaction**. Direct SQL writes to `PhysicalStockBalance` are still allowed in this schema-only phase; only the future command boundary supplies conservation/idempotency invariants.

## State and source rules

- Opening requires an explicit row for **both** warehouse and vehicle, including vehicle `0/0`; a missing opening cannot implicitly obtain a live balance. Opening snapshots and physical movement records have immutable DML triggers. Already loaded manifests and lines cannot be edited or deleted.
- `TripManifest` `LOADED` requires a matching `BusinessSource` with `SourceKind=MANIFEST_LOAD` and the same tenant/Trip. `DRAFT` has no load source. Each loaded line references the same manifest source and a `BusinessSourceLine` with **matching** line number, SKU, base quantity and cost amount. SQL constraints check line-level binding; APP-01B.2 checks that all intended lines, rights and total quantities are complete before committing a loaded header.
- At most one manifest per Trip in APP-01B T0→T2. One right can only be staged once; A and B cannot be relabeled, exchanged or counted twice. Posting a greater quantity than a right's remaining entitlement requires an APP-01B.2 transactional guard (schema checks planned/loaded quantities, not aggregate availability).
- `decimal(18,3)` exact base quantities and `decimal(19,4)` exact synthetic cost amounts; **no** floating point arithmetic, currency conversion, AR, COGS, cash or invoice. `SYNTHETIC_UNITLESS` is the non-monetary test cost unit.
- The immutable source line contains the movement's exact cost in `BusinessSourceLine.Amount`; this is **physical transfer cost only**, not a revenue/receipt posting. The APP-01A `LedgerEffect` financial kinds remain untouched by this migration.

## Verification contract

`App01b1PhysicalStockSchemaTests` adds **7 physical LocalDB tests** sharing the nonce-owned two-tenant fixture:

1. Fresh + repeated migration, required tables/FKs/immutability triggers and version 202611160004 in both tenants.
2. T0 explicit 100/1000 warehouse and 0/0 vehicle while `StockPool` allocations change independently.
3. Invalid warehouse role, wrong Trip vehicle, duplicate Trip manifest and foreign tenant rejected.
4. Valid A40/B30 manifest lines; wrong/right-reused A/B, SKU, Trip, warehouse and tenant rejected.
5. Loaded source identity must match source kind, line number, SKU, quantity and exact cost; duplicate or mismatched physical movement rejected.
6. Negative/invalid qty/cost, zero-quantity cost and opening immutability rejected; exact decimals preserved.
7. Two physical tenant databases may use identical SKU/location IDs while remaining write/read isolated.

**Acceptance boundary:** schema and 7 tests passing on the exact PR HEAD and independent review = `APP-01B.1 SCHEMA_VERIFIED`. It does **not** satisfy APP-01B's 39/39 T0–T2 oracle target. APP-01B.2 implements the transactional load and B31 preflight; APP-01B.3 runs persistence-backed 39/39, concurrency, rollback and full synthetic acceptance. APP-01C–E, complete 91/91, production B, offline writes, EV/P0 and go-live remain **NOT_VERIFIED / NOT_AUTHORIZED**. The Owner 125% browser exception remains `NOT_TESTED`, not PASS.

### Review checklist

- [ ] Final PR HEAD SQL Server LocalDB CI: 49/49 minimum, no skip/fail.
- [ ] No `business/` or `business.lock.json` modifications; no production authority.
- [ ] Confirm migration replay with both tenant nonce databases and negative FK/immutability cases.
- [ ] Verify frontend/PWA, browser 100%, SDK 10.0.404, Node 26 and audit gates remain green.
- [ ] Independent review of FK integrity, source lineage and future APP-01B.2 transaction contract.
