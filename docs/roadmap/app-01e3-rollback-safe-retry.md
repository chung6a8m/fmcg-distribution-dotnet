# APP-01E.3 — Transaction Rollback & Safe Retry Matrix

**State: MERGED / E3_ROLLBACK_RETRY_VERIFIED.** Independently reviewed [PR #35](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/35), source HEAD `430850e0a50289ef99ce702532778b4979f10075`, [Windows Actions #43962422877 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43962422877) **209/209 PASS, 84/84 rollback cases, 5,796/5,796 SQL cells**, TestRunId `app01e3-43962422877-2`, merge commit `d94f90ef3eea0d030bab84d2c604b87770a3fd7d`. Synthetic, nonproduction, two physical nonce-owned SQL Server LocalDB tenants. No new migration, production fault endpoint, application DI switch, business policy change or runtime retries. The locked Business Baseline is unchanged. This increment **does not** claim real TCP loss, process restart, C→P/AC-11 full verification, EV/P0 or go-live.

Prerequisites: [E.1 recovery protocol](app-01e1-recovery-harness.md) and [E.2 SQL interleavings](app-01e2-cross-command-races.md). E.3 proves the before-commit failure half of the recovery contract against the real HTTP → Dapper → SQL persistence path.

## Failure boundary and oracle

- Arm a temporary SQL Server **AFTER INSERT / AFTER UPDATE** trigger on an allowlisted table/verb, throwing SQL error **51063** inside the real **SERIALIZABLE** transaction. Test ownership requires LocalDB, the exact nonce scratch name, a matching App00ScratchMarker and AppTenantBinding. Unexpected pre-existing triggers and unrecognized drop targets fail closed. Removal uses await using. **No production fault endpoint**.
- Read **every row and column** from all dbo user tables in the target scratch database, serialize SQL JSON, sort rows independent of storage order and calculate a full-database **SHA-256 fingerprint**, table count and row count. This detects altered business commands, rights, credit, physical movement, receivables, receipts, source/ledger and cashier/unload—not merely row counts. Run the existing independent 23-field physical/right/AR/COGS/custody/fund SQL probe as a second oracle.
- At the fault point, HTTP 500 or bubbled SQL **51063** is required; database must equal pre-attempt state, with **zero persisted targeted BusinessCommand**. A business REJECTED or an unexercised trigger yielding HTTP 200 is not accepted.
- Disarm the trigger, retry the **exact original** tenant/producer/command/actor/source/payload from a new authenticated client. Require APPLIED, Replayed=false, one linked SQL source with expected line/effect cardinality. A second identical request must return Replayed=true and leave the full SQL unchanged. A changed SourceNumber using the **same** command identity must return HTTP **409 / CONFLICT / IDEMPOTENCY_PAYLOAD_MISMATCH**, also with no new changes.
- Failure before COMMIT erases the idempotency key: retry is a **fresh apply**, not a replay. The durable REJECTED response is a different terminal case. Actual lost response/restart/outcome lookup belongs to E.4.

## SQL fault matrix (42 fault points × 2 physical tenants = 84 cases)

| Command family | AFTER INSERT fault | AFTER UPDATE fault | Meaningful rollback boundary |
|---|---|---|---|
| A reserve (6) | BusinessCommand, BusinessSource, StockRight, LedgerEffect | StockPool, **BusinessCommand finalization** | identity, allocated pool, right/effect, final pre-COMMIT update |
| B quota (3) | BQuota, LedgerEffect | StockPool | quota child, allocation capacity and ledger |
| A confirm (2) | AConfirmedLineSnapshot | AOrder | frozen terms/price and order status |
| B credit (3) | BCreditGrant, LedgerEffect | BCreditPool | grant/approved pool |
| T2 manifest load (5) | PhysicalStockMovement, LedgerEffect | TripManifest, TripManifestLine, PhysicalStockBalance | source-bound stock transfer |
| T3 B20 (8) | SaleDelivery, ReceivableOpenItem, CashReceipt, ReceiptAllocation, BGrantConsumption, LedgerEffect | StockRight, PhysicalStockBalance | right, physical, AR, verified receipt, B-credit consumption |
| T4 A25 (5) | ReceivableOpenItem, LedgerEffect | StockRight, PhysicalStockBalance, AOrder | right, COGS/AR and partial order state |
| T5 cashier OFFER (1) | CashHandoverOffer | — | source-only, non-posting OFFER |
| T5 cashier ACCEPT (2) | CashHandoverAcceptance, LedgerEffect | — | independent cash witness, CUSTODY-OUT/FUND-IN |
| T6 unload (7) | TripUnload, TripUnloadLine, LedgerEffect | StockRight, StockPool, PhysicalStockBalance, Trip | return, B-right release, pool capacity, trip status |

An SQL AFTER trigger fires after the selected DML statement but before COMMIT. A table can be touched repeatedly; this tests the **first qualifying statement** for each point, not every ledger leg or every possible scheduler interleaving. It is statement-boundary coverage, not exhaustive instruction-level fault injection.

## Deterministic setup and guards

- Reuse E.2's synthetic two-tenant scenario builder, authenticated actors/roles and independent SQL source audit. Each case gets a fresh trip, SKU, location and A order. B20/A25/cashier/unload prepare valid rights, manifest, approvals and cash witnesses through the real application API.
- The fault must leave neither a RECEIVED command nor orphan source/effect. The recovery retry must preserve command identity and actor; changed canonical payload is rejected even after a successful retry.
- A guard-negative test refuses master, non-LocalDB hosts, cross-tenant binding and unknown table/verb, and never DROP/RESETs an external database.
- Preserve E.2 16 contested two-client tests, E.1 six recovery smoke reports, APP-01B.3 **39/39**, APP-01C.4 **65/65**, APP-01D **91/91** SQL oracles per tenant, tenant/role isolation, business lock, .NET build, MTP/TRX/Cobertura, npm audits, React/PWA and Playwright at 100%.

## Evidence contract and acceptance

- **84 JSON files**: TestResults/APP-01E3/tenant-a|b-scenario-table-verb-rollback.json; schema APP01E3_ROLLBACK_V1. Each records TestRunId, real PR source HEAD separate from workflow SHA, nonce tenant DB, fault table/verb, observed fault, actor/command/source and independent SQL audit, snapshots **Before / AfterFault / AfterCommit / AfterReplay / AfterConflict**.
- Each report contains **23 actual/expected/signed-diff fields** per rollback, replay and conflict stage (69 per report, **5,796 SQL cells total**). Windows CI must independently require exact 84 filenames, schema and provenance, correctly owned LocalDB, source lineage, HTTP 200/200/409, matching rollback/replay/conflict full-table fingerprints, changed commit fingerprint, 69 distinct cell names and zero signed differences.
- **Minimum 209 tests** (previous 124 + 84 physical SQL cases + 1 guard), zero failures/skips. CI writes APP-01E3-TestRunId.txt. Mark **E3_ROLLBACK_RETRY_VERIFIED** only after independent review and green Actions on **exact final PR source HEAD**; each code change resets the review/CI gate.
- Keep synthetic unit SYNTHETIC_UNITLESS, invoiceEligible=false, no real cash/payment/invoice accounting, production B, offline writes, EV/P0, 125%-scale (Owner-accepted exception) or tenant go-live.

**Next:** E.4 actual disconnected transport/unknown durable outcome/restart/lookup; E.5 independent cross-transaction reconciliation. E.3 SQL-trigger results are not E.4/E.5 or full AC-11 acceptance.
