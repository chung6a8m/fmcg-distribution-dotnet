# APP-01B.2 — Transactional LoadManifest and B31 no-effect quota rejection

**Status:** IMPLEMENTED / PR CI AND INDEPENDENT REVIEW PENDING (2026-11-16). Synthetic SQL Server LocalDB only. APP-01B.1 is [SCHEMA_VERIFIED](app-01b1-physical-stock-schema.md); APP-01B.3's **39/39 AC-03 T0–T2 golden oracle remains NOT_RUN**. The immutable baseline in `business/` and `business.lock.json` is unchanged.

## Transactional contract

New synthetic-only authenticated routes (only with enabled Development/IntegrationTest signed JWT, server-resolved nonce-owned tenant LocalDB):

| Route | Policy | Outcome / effect |
|---|---|---|
| POST `/api/synthetic/app01b/commands/load-manifest` | `SyntheticWarehouse` | `LoadManifestCommand(ProducerId, CommandId, SourceNumber, ManifestId)`, `APPLIED/MANIFEST_LOADED` or durable `REJECTED` |
| POST `/api/synthetic/app01b/commands/preflight-b-sale` | `SyntheticSales` | `PreflightBSaleCommand(ProducerId, CommandId, SourceNumber, TripId, SkuId, BaseQuantity, UnitPrice, OfferedCashAmount)`, durable **`REJECTED/REJECTED_QUOTA/B_QUOTA_INSUFFICIENT`** for B31/15/465; within-quota also **rejected** (`B_SALE_NOT_IMPLEMENTED`) until APP-01C |

- Both routes use APP-01A's **same** canonical payload hash v1 and business command identity `(tenant,producer,command)` with the **same transaction-owned app-lock namespace**. Same payload replays the completed outcome and source, changed payload responds HTTP 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`. A valid rejected business command persists its reason; a SQL exception rolls its command row back along with every effect.
- `LoadManifest` explicitly starts a Dapper `SERIALIZABLE` SQL transaction. It validates one DRAFT manifest attached to a PREPARED Trip, active warehouse and vehicle, staged lines and exact A/B rights (confirmed A order or B quota, HELD state, matching Trip/SKU/location and non-consumed entitlement). It additionally verifies source lines from original STOCK_ALLOC sources and confirmed A order-line SKU lineage. It acquires manifest and right update locks and warehouse/vehicle physical balance update locks ordered per SKU.
- For **every** staged line, it posts the full planned quantity, not a request-selected partial line. Warehouse and vehicle must have explicit opening/balance rows. Warehouse debit and vehicle credit update **both** decimal(18,3) quantity and decimal(19,4) cost. Cost is based on carried warehouse average, rounded to 4 decimals and distributed deterministically with last-line residual; full depletion transfers exact remaining cost. No negative or orphan cost and no implicit opening.
- The same commit creates `MANIFEST_LOAD` `BusinessSource`, one exact `BusinessSourceLine` per line, loads the header and all lines, creates one append-only `PhysicalStockMovement` per line and balanced `LedgerEffect` pairs (`PHYSICAL_STOCK`, `PHYSICAL-OUT` / `PHYSICAL-IN`). A/B rights, StockPool allocation and approval/credit remain **unchanged**. No sale, receipt, cash/custody, AR, revenue, invoice or COGS is posted.
- `PreflightBSale` verifies base quantity/price/offered amount with **exact synthetic decimals**, prepared Trip and loaded B rights; B31 exceeds B30 and returns durable `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT` without **any business source/line/effect/balance/movement/receipt**. The offered 465 is **not received cash**; preflight is not a successful B sale. The result itself is an idempotent `BusinessCommand` row.

## Verification matrix and remaining boundary

Eleven new physical LocalDB tests (`App01b2ManifestCommandTests`) cover (1) T1 MAIN100→30/TRIP0→70, cost1000→300/0→700, A40/B30 and 4 balanced ledger legs; (2) B31/15/465 durable rejection, replay/conflict, complete business no-effect and B20 fail-closed; (3) manifest replay and cross-command hash collision; (4) unconfirmed A right; (5) absent/short physical opening; (6) SQL-trigger injected failure after first movement with full rollback and retry; (7) two-client concurrent loads; (8) JWT role and two physical tenant boundaries; (9) full-depletion fractional-cost residual; (10) forged stock-allocation source lineage rejection; (11) confirmed A order line SKU mutation rejection. CI floor increases 49→**60** tests; TestRunId `app01b2-<run>-<attempt>`, TRX/Cobertura and browser/PWA artifacts remain required.

**Verification only when exact PR HEAD CI succeeds:** implementation and matrix are proposed changes, not yet a PASS. A final independent review must inspect race ordering, rollback and SQL integrity; record actual CI/TestRunId and any findings before APP-01B.2 is closed.

**Not in scope:** full B20/A25 sale/delivery/AR/COGS/cash (APP-01C), physical handover/return (APP-01D), APP-01B.3 persistence-derived **39/39 golden comparison**, full **91/91** (APP-01D/E), real B_ONLINE/offline, EV/P0, tax/legal invoice, production or pilot. 125% browser remains Owner `EXCEPTION_ACCEPTED / NOT_TESTED`, not a PASS.
