# APP-01A.1 — Integrated schema / physical LocalDB contract

**Status:** schema implemented and physical LocalDB guards verified in [PR #21](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/21); APP-01A overall **NOT_VERIFIED** pending APP-01A.2/.3. Owner-authorized **synthetic/nonproduction only**. Related: [APP-01A issue #2](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/2), [roadmap](app-00-app-01.md), [Owner technology decisions](../decisions/owner-app-2026-11-15.md).

## Goal and precise boundary

Create the **first integrated application schema** on each nonce-owned SQL Server LocalDB tenant database, rather than copying scratch `ac03/ac09/ac21/ac06` schemas. The existing APP-00A `AppTenantBinding` is the authoritative one-row binding: the new unique TenantId key and every root-table FK reject a wrong-tenant write even through direct SQL. Every cross-reference uses the (`TenantId`, `Id`) parent key and Customer+ShipTo pair references the triple (`TenantId`, `CustomerId`, `Id`).

**Tables (15):** `Customer`, `ShipToSnapshot`, `Sku`, `StockLocation`, `Trip`, `BusinessCommand`, `BusinessSource`, `BusinessSourceLine`, `AOrder`, `AOrderLine`, `StockRight`, `AReservation`, `BQuota`, `BCreditGrant`, `LedgerEffect`. IDs are application-supplied GUIDs; mutable records carry SQL Server `rowversion`. Quantities use `decimal(18,3)`, money/synthetic units `decimal(19,4)`, UoM conversion factor `decimal(19,6)`.

## Database guarantees now

- Tenant binding and parent references; Customer and ShipToSnapshot must agree on every AOrder and BCreditGrant.
- `BusinessCommand` has a unique (`TenantId`, `ProducerId`, `CommandId`) identity and fixed 32-byte payload hash with explicit hash version. The schema **does not** compute a canonical hash or complete the replay workflow.
- `BusinessSource` and `BusinessSourceLine` reference the originating command and stable document/line identity; `LedgerEffect` has unique (`TenantId`, `BusinessSourceLineId`, `EffectKey`) so the same effect key cannot post twice.
- `StockRight` is the shared A/B rights namespace, with exactly one mode (`A` or `B`) per row and unique source line; `AReservation` can link only to `A` rights, `BQuota` only to `B` rights. Per-row consumed+released amount may not exceed grant; BCreditGrant has equivalent amount guards.
- All new data remains in separate physical tenant databases. Existing non-production scratch marker, nonce, instance and ownership guards are unchanged.

## Explicitly **not** guaranteed yet

Schema checks alone do **not** prevent the *sum of independent* A/B allocations for a (`StockLocationId`, `SkuId`) from exceeding physical stock, nor do they verify a StockRight's Trip, SKU and order-line consistency. APP-01A.2 must serialize allocation across both rights kinds with an explicit Dapper SQL transaction and authoritative stock snapshot; APP-01A.3 must add two-client races/fault rollback tests.

No `ConfirmAOrder`/`ReserveAStock`/B grant command, payload hash canonicalizer, durable outcome mapping, audit query service, AR, COGS, B sale/receipt, API endpoint or frontend workflow is introduced by APP-01A.1. No production SQL, real authentication, B_ONLINE external integration, offline business write, EV/P0 or go-live.

## Verification matrix / PR exit

1. On **two distinct, nonce-owned LocalDB tenant databases**: fresh migration and two safe repeated invocations; one applied `202611160001` version per database, exactly one matching tenant binding.
2. Same Customer ID/code in separate DBs remains separate; wrong TenantId direct SQL insert fails, rowversion changes on update.
3. Wrong Customer/ShipTo triple fails for AOrder and BCreditGrant; valid order/line/grant paths persist, grant over-consumption fails.
4. Same source-line duplicate StockRight fails; AReservation cannot reference a B right and BQuota cannot reference an A right; right over-consumption fails.
5. Duplicate (`TenantId`, `ProducerId`, `CommandId`) and duplicate ledger effect key fail; zero-value effect fails; no implicit AR/COGS posting.
6. Verify immutable Business Baseline, .NET build, >=14 xUnit/LocalDB tests (9 foundation + 5 schema), browser/PWA regression, npm audits, PR final-HEAD CI artifacts. **PR #21 merge requires independent review and green final-HEAD CI; this schema increment does not complete APP-01A acceptance.**

## Follow-on scope

APP-01A.2 implements commands and their guarded explicit transactions. APP-01A.3 closes the original Issue #2 acceptance with replay, changed-payload no-effect conflict, concurrent stock/grant races, rollback and audit evidence. APP-01B..E continue the locked golden T0–T6 scenario; this PR cannot claim 91/91 app checkpoints.
