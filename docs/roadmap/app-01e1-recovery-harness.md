# APP-01E.1 — Recovery contract and deterministic test harness

**Scope:** synthetic/nonproduction test infrastructure, separate reviewable increment. **Status: IMPLEMENTED / INDEPENDENT_REVIEW_AND_FINAL_HEAD_CI_PENDING.** This contract does **not** claim that the full APP-01E race matrix, AC-11, disconnected network, real client/server restart, C→P or production recovery has passed.

Canonical application base: APP-01D [PR #32](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/32), independently reviewed source HEAD `d32cd507`, [Actions #43878243524 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43878243524): **99 tests, 91/91 persisted AC-03 cells per LocalDB tenant**. Source: immutable [AC-03](../../business/reference-data/expected-results/golden-oracles.json), [APP-01D contract](app-01d-cashier-unload.md), shared `BusinessCommand`/`BusinessSource`/`BusinessSourceLine`/`LedgerEffect`. No edits to `business/` or `business.lock.json`.

## Five increment boundaries

| Increment | Primary deliverable | Exit gate |
|---|---|---|
| **APP-01E.1** | Recovery protocol, two-client admission barrier, test-only pre-COMMIT SQL fault and post-response ACK suppression, persisted SQL actual/diff + CI evidence | 6 parameterized LocalDB smoke cases (3 scenarios × two physical tenants), existing regressions, independent HEAD review and CI |
| **APP-01E.2** | Cross-command two-client races over A/B rights, manifest, B20, A25, cashier and unload | Each interleaving linearizes to valid outcome, no double posting/oversell, full per-source reconciliation |
| **APP-01E.3** | Per-command fault-point rollback/retry matrix spanning every source/physical/financial transaction | No partially committed source or ledger/physical/monetary state; precise retry and conflict semantics |
| **APP-01E.4** | Command result lookup/recovery protocol and client/server restart; HTTP timeout/lost ACK behavior | Durable outcome recovery without duplicate business writes after process restart, with access control and unknown-state handling |
| **APP-01E.5** | Cross-transaction independent reconciliation and closeout | Existing AC-03 **91/91 per tenant** survives the combined suite, independent SQL source/ledger fingerprint, exact reviewed CI HEAD |

Keep each increment in a separate PR; do not silently promote E.1 harness observations to E.4 recovery acceptance.

## Recovery protocol baseline (normative for later increments)

1. A logical command is identified by **(server-validated TenantId, ProducerId, CommandId)**. Persist these at the client before sending; keep the **same** SourceNumber, canonical payload (including all fields) and signed actor for retries. Do **not** assign a fresh CommandId on HTTP timeout, cancellation or lost response.
2. The existing server hashes command kind, tenant, actor, source number and named fields with SHA-256 canonical format v1. Same identity+same canonical payload may return a durable `APPLIED` or `REJECTED` with `Replayed=true`; changed payload or actor returns `CONFLICT`/HTTP 409 and **zero new effects**.
3. The current implementation inserts and finalizes `BusinessCommand` in one explicit SQL **SERIALIZABLE** transaction with its source/ledger/stock/financial effects. A fault **before COMMIT** rolls back command identity and all effects. Retry is permitted with the same identity; a re-executed command is **not** `Replayed=true` because no outcome survived.
4. An ACK lost **after COMMIT** makes the **client-visible result unknown**, not the SQL result. Never interpret a network exception as `REJECTED` or as permission to use a new identity. A same-identity retry may expose the committed outcome. The future E.4 read-only outcome lookup must be authenticated, tenant/actor-scoped, and return explicit `UNKNOWN/NOT_FOUND` without making business writes. This lookup **does not exist in E.1**.
5. Business rejection is a durable terminal outcome; transport failure/HTTP 5xx is not a business rejection. Do not automatically retry all 409/4xx errors. Do not auto-post offline writes. Synthetic routes remain Development/IntegrationTest-only, nonce-owned LocalDB-only and JWT role-bound.
6. Recovery cannot infer correctness from HTTP alone: independently query persisted command, source, source line, ledger effect, rights, allocation pool and cross-module state. Check source/effect uniqueness, quantity/cost conservation, amount balance and actor/tenant provenance as later increments expand.

## Deterministic harness architecture

All E.1 code is in **`tests/FmcgDms.Api.Tests/`**, without application runtime DI switches, schema migration changes or production fault endpoints:

- `TwoClientReleaseGate` + `TwoClientGateHandler`: create **two separate authenticated `HttpClient` instances** with a one-shot `TaskCompletionSource` admission barrier; wait for **exactly two arrivals** (with bounded cancellation/timeout) and release them together. This controls *HTTP request admission*, **not** the SQL scheduler or transaction interleaving; E.2 must introduce controlled SQL/resource contention to force target race schedules.
- `PreCommitLedgerFault`: temporary SQL Server LocalDB `AFTER INSERT` trigger on `LedgerEffect`, scoped to a **single test-generated SourceNumber and tenant**, throws **51061**. It fires after prior stock pool, source, source line and right writes, but before COMMIT. Drop in `finally` via `IAsyncDisposable`. Never use on real databases; the existing `SqlScratchFixture` verifies `(localdb)\...`, nonce-named/marked tenant databases before opening the API.
- `AfterServerAckLossHandler`: lets TestServer **finish** a POST, then disposes and suppresses **one response** with a synthetic `HttpRequestException`. This proves the SQL server may have committed despite no response reaching that test client. It is **not** a network-disconnection or process-crash emulator, nor does it test E.4 restart.
- `RecoverySqlProbe`: read DB_NAME(), keyed `BusinessCommand`, linked `BusinessSource`, `BusinessSourceLine`, `LedgerEffect`, `StockRight` and scoped `StockPool` **from SQL**; materialize an ordered SHA-256 ledger fingerprint. Do not derive actuals from HTTP results, service mocks or expected values.
- `RecoveryHarnessEvidence`: write `TestResults/APP-01E1/{tenant}-{scenario}-harness.json` with schema `APP01E1_HARNESS_V1`, `TestRunId`, source HEAD, workflow SHA, database, stage and **six numeric SQL expected/actual/diff cells** per stage. The CI must independently validate file presence, provenance, exact case/tenant set, stages, cells, differences and PASS status.

All test cases use signed synthetic `warehouse` JWTs and `GrantBQuota` as the **harness smoke command**, not as evidence that E.2's A/B race matrix or E.3's other posting transaction fault points have passed.

## E.1 repeatable smoke cases and expected SQL

| Scenario (each tenant-a and tenant-b) | Stages | Required SQL actuals |
|---|---|---|
| `two-client-gate` | `AFTER_BOTH_HTTP_CLIENTS` | one `APPLIED` BusinessCommand, Source/Line/RIGHT_B effect/StockRight **1 each**, allocated **30**, responses one first-apply and one replay, same SourceId |
| `precommit-rollback` | `FAULT_PRECOMMIT_ROLLED_BACK`; `RETRY_SAME_IDENTITY` | before trigger drop: **0 command/source/line/effect/right, 0 allocated**; after retry: **1 each, allocated 30**, fresh `Replayed=false` |
| `lost-ack-smoke` | `POSTCOMMIT_ACK_SUPPRESSED`; `SAME_IDENTITY_REPLAY` | SQL already has **1 command/source/line/effect/right, allocated 30** before replay; after replay identical source ID, counts and ledger digest, `Replayed=true` |

Evidence cardinality is **2 tenants × (1+2+2) stages × 6 SQL numeric cells = 60 cells**. The six E.1 test invocations and their **60 probe cells are separate** from AC-03's **91×2** business oracle; the existing oracle must continue to run unchanged. SQL collection is *not* independent APP-01E.5 cross-transaction reconciliation.

### Failure matrix for handoff

| Failure point | E.1 mechanism | E.1 claim | Full verification owner |
|---|---|---|---|
| Two client requests admitted together | explicit TaskCompletionSource barrier | Same-key dedup under concurrent transports, SQL source once | E.2 forces cross-command SQL interleavings |
| After stock/source/right, before ledger COMMIT | source-scoped LocalDB ledger trigger | Atomic rollback on this chosen command; retry of same key | E.3 covers all transaction/fault points |
| After handler completes, before client reads response | one-shot DelegatingHandler suppression | SQL committed despite client-visible exception; replay stable | E.4 exercises actual interrupted connections and restarts |
| After COMMIT, client/server restarted | **not implemented** | **NOT_VERIFIED** | E.4 |
| Reconciliation across T0–T6 and C→P | existing APP-01D oracle only | **NOT_VERIFIED** for E.5/AC-11 full | E.5 / later C→P implementation |

## CI and security gates

- Windows `windows-2022`, .NET SDK `10.0.404`, xUnit v3/MTP v2, physical SQL Server LocalDB. Keep the *minimum 99 prior tests*, plus **six new Theory cases** (new minimum **105**), no skips, fail on non-zero exit.
- Require **exactly six** `*-harness.json` artifacts: `tenant-a|tenant-b` × `two-client-gate|precommit-rollback|lost-ack-smoke`. Check `APP01E1_TEST_RUN_ID` (run ID + attempt), real PR source HEAD vs synthetic workflow merge SHA, nonce DB name with correct tenant suffix, report shape, case stage counts, 6 cells each and signed numeric zero differences.
- Retain MTP TRX/Cobertura, APP-01B.3 39/39, APP-01C.4 65/65, APP-01D 91/91 **per tenant**, browser/PWA/Playwright at 100%, immutable baseline audit. Attach artifact URLs, PR-head CI and `TestRunId` before independent review/merge; rerun after any changed HEAD.
- No production B, offline business writes, real cashier/payment/invoices, policy signoff, EV/P0 or go-live. `SYNTHETIC_UNITLESS`, `invoiceEligible=false`, and Owner's **125% display EXCEPTION_ACCEPTED/NOT_TESTED** remain unchanged.

**Exit decision:** only `E1_HARNESS_VERIFIED` after review and exact-HEAD CI success. Until then `IMPLEMENTED / CI_PENDING`. Full APP-01E and AC-11 remain `NOT_VERIFIED`.
