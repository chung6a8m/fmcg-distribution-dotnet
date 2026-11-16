# APP-01A.2 — Command transactions and shared A/B allocation

**Status: IMPLEMENTED / INDEPENDENT REVIEW FINDING FIXED / CI_VERIFIED.** Synthetic/nonproduction increment of [Issue #2](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/2); see [APP-01A.1 schema](app-01a1-integrated-schema.md). APP-01A is now **SYNTHETIC_VERIFIED** after independently reviewed APP-01A.3 merge and main CI; see [closeout](../closeout/2026-11-16-app-01a.md). No change to `business/` or `business.lock.json`.

## Behavior and allocation authority

FluentMigrator `202611160002` introduces:

- `StockPool(TenantId, StockLocationId, SkuId)`: explicit synthetic, source-backed **opening snapshot** `OnHandBaseQuantity` and `AllocatedBaseQuantity`. No row means **no stock allocation authority**. Only active warehouse SKU/location is eligible. AReservation and BQuota both increment this **same** allocation counter in one SERIALIZABLE Dapper transaction with `UPDLOCK,HOLDLOCK`, a conditional update, and matching StockRight/source/effect inserts. StockPool CHECK enforces `0 <= allocated <= onHand`. The pool is not physical stock ledger and never silently invents on-hand inventory.
- `BCreditPool(TenantId, TripId, CustomerId, PoolCode, CurrencyUnit)`: **explicitly seeded synthetic credit approval capacity**, never inferred from missing/unset policy. No row denies the grant. Scoped to `B` and `SYNTHETIC_UNITLESS`; granted sum cannot exceed approved cap. This is **not real/customer-approved credit policy**.

Both tables belong to a single independently bound tenant database. Existing `AppTenantBinding`, FK lineage, unique source/right/effect keys and nonce-owner LocalDB connection guards remain authoritative.

### Commands (synthetic endpoints only)

`POST /api/synthetic/app01a/commands/{reserve-a-stock|confirm-a-order|grant-b-quota|grant-b-credit}` requires validated signed synthetic JWT with server-resolved tenant and actor. Routes are **not registered** unless `SyntheticAuth:Enabled` is set in `Development` or `IntegrationTest`; `TenantConnections.OpenSyntheticScratchAsync` again checks LocalDB, instance, exact nonce marker, slot and tenant binding. Request cannot set tenant, actor or connection string.

All requests carry `ProducerId`, `CommandId`, `SourceNumber`. All four commands require a `PREPARED` Trip with an **active `VEHICLE` StockLocation**; a warehouse or inactive location in `VehicleLocationId` yields durable `TRIP_NOT_PREPARED` without effects. Other request properties:

| Command | Payload | Successful outcome / invariant |
|---|---|---|
| `ReserveAStock` | `OrderId`, `OrderLineId`, `StockLocationId`, `BaseQuantity` | `A_STOCK_RESERVED`: DRAFT order for an active Customer with prepared Trip, matching line SKU, <= remaining order qty, eligible warehouse pool available; inserts A right/reservation |
| `ConfirmAOrder` | `OrderId` | `A_ORDER_CONFIRMED`: requires all lines priced and fully covered by held A reservations for same SKU/trip. Transitions DRAFT→CONFIRMED; no physical stock delta, AR, revenue, invoice or COGS |
| `GrantBQuota` | `TripId`, `StockLocationId`, `SkuId`, `BaseQuantity` | `B_QUOTA_GRANTED`: prepared Trip, same common stock pool; inserts B right/quota |
| `GrantBCredit` | `TripId`, `CustomerId`, `ShipToSnapshotId`, `PoolCode`, `CurrencyUnit`, `Amount` | `B_CREDIT_GRANTED`: matching active Customer + ShipTo, prepared Trip, preseeded B credit pool capacity |

`ReserveAStock` intentionally precedes `ConfirmAOrder` in this API; confirmation refuses an incomplete hold. The fuller business workflow (terms, tax/discount, gift, lot/expiry/FEFO, actual stock custody move, B sale and AR) is **not claimed** and must be implemented/verified separately. There is no arbitrary on-hand or credit-limit mutation endpoint.

## Atomicity and duplicate contracts

- One explicit `IsolationLevel.Serializable` SQL transaction: serialize command identity via transaction-owned `sp_getapplock`, re-read command row with `UPDLOCK,HOLDLOCK`, verify hash, then persist result and all source/right/effect changes together. No independent repository save/transaction.
- `PayloadHashVersion=1`: SHA-256 of UTF-8 canonical JSON with fixed ordered `Version/Kind/TenantId/ActorId/SourceNumber/Fields`, N-format GUIDs and invariant exact decimal representation. Never hash raw JSON order or JS floats. Future format changes require new version and compatibility code.
- Idempotency key = `(TenantId,ProducerId,CommandId)`. **Same hash** returns original `APPLIED` or `REJECTED`, outcome/reason and source ID with `Replayed=true`, no writes. **Different hash or version** returns HTTP 409 `CONFLICT / IDEMPOTENCY_PAYLOAD_MISMATCH`, without altering original state. Malformed command identity returns 400 and is not stored.
- Business negatives persist `REJECTED / reason` in `BusinessCommand`, committed with no rights/source/effects. Examples: `STOCK_INSUFFICIENT`, `STOCK_POOL_NOT_FOUND`, `SOURCE_ALREADY_USED`, `A_LINE_OVER_RESERVED`, `ORDER_NOT_FULLY_RESERVED`, `CUSTOMER_SHIPTO_MISMATCH`, `POOL_UNSUPPORTED`, `CREDIT_LIMIT_INSUFFICIENT`. HTTP 200 denotes **a durable outcome, not necessarily business approval**; always inspect `Status` and `ReasonCode`.
- A and B stock grants share `SourceKind=STOCK_ALLOC` and the `BusinessSource(TenantId,SourceKind,SourceNumber)` unique identity; reusing an external stock source in a *different* command/mode is rejected without allocation. A confirmed order and B credit grant use their own source namespaces. `LedgerKind=RIGHT_A/RIGHT_B/RIGHT_CREDIT_B` captures the **entitlement** only, not a physical inventory movement or AR/COGS posting.
- SQL faults roll back the allocation counter and `BusinessCommand/BusinessSource/BusinessSourceLine/StockRight/ARservation/BQuota/BCreditGrant/LedgerEffect`. After a rollback, no durable outcome is claimed; retry can use the same key.

## Independent review and CI evidence (PR #22)

- Independent P2 finding: a `PREPARED` Trip could reference a warehouse or inactive vehicle; all four commands now verify active `VEHICLE` location, returning `TRIP_NOT_PREPARED` without rights/credit effects. [Fix commit `fd93228`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/fd93228f2f2338004b855d04e8481064cb803c42).
- [Reviewed code HEAD CI #43792122101](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43792122101) **SUCCESS**: .NET build 0 warnings / 0 errors, 25/25 xUnit v3 / physical LocalDB (0 failed/skipped), immutable Business Baseline, npm security gates, frontend/PWA and 5/5 Playwright. PR completion requires fresh CI on the final documentation HEAD before merge.

## Verification contract and known limits

Physical Windows LocalDB tests cover migration replay in both nonce-owned DBs; A reserve→B quota→A confirm; no AR/COGS; source reuse, shared stock bounds, invalid cross-tenant resources, customer/ShipTo/pool, credit cap; successful and rejected idempotent replay/changed payload conflict; concurrent two-client stock and retransmit; inactive/wrong-kind Trip vehicle rejection across all commands; injected SQL-trigger rollback. Require **>=25 xUnit tests** (14 prior + 10 APP-01A.2), .NET build and PR-head CI artifacts, unchanged Business Baseline and frontend/Playwright checks.

**APP-01A.3 follow-on completed:** [PR #23](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/23) independently reviewed, four findings fixed/resolved, final PR-head CI [#43800609004](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43800609004) 42/42 LocalDB and 5/5 Playwright, TestRunId `app01a3-43800609004-1`; main merge CI [#43801711102](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43801711102) successful. [Closeout](../closeout/2026-11-16-app-01a.md).

**Remaining for APP-01B..E:** manifest/physical custody transfer; sales/delivery/cash/AR/COGS/invoice; 91/91 AC-03 persistence checks and cross-module recovery. APP-01B [proposal](app-01b-manifest-transfer-proposal.md). No EV/P0, real B_ONLINE, offline write, production import or tenant go-live authorization.
