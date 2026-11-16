# APP-01A.3 — Synthetic behavior hardening, as-of audit, approval and tenant matrix

**State on PR #23: IMPLEMENTED / CI_VERIFIED (code HEAD) / INDEPENDENT_REVIEW_PENDING.** Part of [Issue #2](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/2); APP-01A overall is **NOT_VERIFIED** until an independently reviewed final PR HEAD passes all gates. All work remains synthetic and nonproduction. The immutable Business Baseline and lock are unchanged.

## Security/authority boundary

- The four synthetic command POST routes and the audit GET route exist **only** in Development/IntegrationTest with `SyntheticAuth:Enabled=true`. Production with synthetic auth disabled returns 404; no production authentication, pilot or tenant admission is introduced.
- Signed JWT must have matching nonempty `sub` / `actor_id` and `tenant_id` (server-resolved). Claims `synthetic_role=warehouse` permit `ReserveAStock`, `GrantBQuota`, `GrantBCredit`; `synthetic_role=sales` permits `ConfirmAOrder`; `synthetic_role=auditor` permits GET audit only. No implicit role from possession of a tenant token; HTTP and service-layer checks are separate.
- Real role/assignment/cross-seller authorization is **not implemented**. These synthetic scopes are not a substitute for production identity, Trip Writer assignment, AP-09 mode release or tenant-go-live gates. Replays require current authorization as well as the original command key/hash. Unauthenticated → 401; authenticated wrong role → 403; unknown/misbound/non-LocalDB scratch tenant → 403; unknown/missing route in disabled host → 404.
- All reads/writes open only the nonce-owned, slot-matched, tenant-bound LocalDB configured **server-side**. A user-supplied `X-Tenant-Id`, query `tenantId`, source ID, connection string or actor field cannot reroute an API call.

## Synthetic AP-01 credit policy evidence

Migration `202611160003` adds `CreditPolicyApproval` linked to the exact `BCreditPool` scope and nullable `BCreditGrant.ApprovalId` for upgrade compatibility. Each approval stores Tenant, Trip, Customer, Pool, Currency, CaseNumber, PolicyVersion, ProposedBy, CheckedBy, Decision, Reason, EvidenceRef, ApprovedLimit, EffectiveFrom/Until and ReviewedAt. A composite grant FK binds the same scope; SQL prevents an `APPROVED` row without an independent checker, reason, evidence and review time.

`GrantBCredit` in its existing serializable Dapper transaction requires exactly one **active APPROVED** matching row, with a checker distinct from proposer **and current command actor**, a canonical whitespace-free actor/checker identity, a review timestamp no later than the transaction's UTC cutoff, and approved policy cap at least equal to the explicit synthetic pool cap. No approval → `CREDIT_APPROVAL_REQUIRED`; two active approvals → `CREDIT_APPROVAL_AMBIGUOUS`; checker conflict → `CREDIT_CHECKER_CONFLICT`; cap mismatch → `CREDIT_POLICY_LIMIT_MISMATCH`. Existing `CREDIT_POOL_NOT_FOUND`, `CREDIT_LIMIT_INSUFFICIENT`, cross-customer/ShipTo and idempotency errors remain. Denials are durable `REJECTED` with no source/rights/effect, not an automatic grant or HTTP 403.

**UNSET ≠ APPROVED=0.** The approval seed is an explicit fixture injected **only** by the test harness; there is no public seed/update/auto-approve endpoint, no currency normalization, no real finance approval workflow or policy threshold assumption. `AP-01` is the credit control covered here; AP-02..10, real pricing/terms/discount, Trip Writer assignment and seller/mode/legal controls remain separately gated. Do not treat `BCreditPool.ApprovedLimit` alone as an authorization.

## Read-only logical audit asOf

`GET /api/synthetic/app01a/audit/as-of?asOf=2026-11-16T02%3A00%3A00Z`

Requires an explicit valid UTC/offset timestamp and an auditor role; invalid or >1-minute future cutoff returns 400. The response includes:

- `TenantId`, normalized `AsOfUtc`, commands with actor, producer, command identity, submitted/completed time and logical status/reason/outcome; `Pending` includes persisted `PENDING/RECEIVED` and `PENDING_AS_OF` when the completion timestamp is later than cutoff.
- Source IDs, kind/number, actor/customer/trip, recorded timestamp, `SourceLines` (line IDs, SKU, quantities/amounts) and effect counts; per-line `StockRights` (line ID, right kind, warehouse/SKU, granted quantity), source-backed ledger effects, and B credit grant/approval case/policy/checker/evidence lineage.
- `StockAllocations`: grant quantity versus `RIGHT_A/RIGHT_B` ledger-effect quantity **as of the same cutoff** by (warehouse, SKU), and `ReconciliationIssues` for missing applied source, missing source lines, unmatched per-line stock-right/credit-grant effects (even when pool totals cancel), orphan effects or missing approval. `A_CONFIRM` correctly carries source lines but **no** AR, COGS or physical stock effect.

Audit runs one SQL `Serializable` read transaction across all result sets and filters future-completed transactions and future effects. It is a **logical completed/recorded timestamp view, not SQL Server temporal/commit-time history**: `PENDING_AS_OF` is an inference, not proof that an uncommitted request was durably visible at cutoff; mutable policy/stock-pool history is not reconstructible. Do not compare present `StockPool` counters to a historical asOf or use audit as evidence of real accounting. No raw SQL endpoint or arbitrary tenant filter.

## Physical LocalDB verification matrix

17 APP-01A.3 tests over the 25 prior tests (expected minimum **42**, xUnit v3/MTP v2) cover:

| Case group | Expected result |
|---|---|
| Schema/policy | Migration replays independently in A/B; self-approval and foreign tenant FK fail; pending/rejected/expired/missing/ambiguous/over-limit/future-dated review/actor-checker conflicts deny; noncanonical checker and signed actor whitespace rejected; referenced approval edit/delete denied |
| Audit cutoff/graph | Before/after status, source actor/line IDs and lineage, credit approval evidence, A/B per-line right/effect mismatch even with balanced pool sums, no AR/COGS; explicit PENDING and later-completion hiding; UTC/offset parsing negatives |
| HTTP matrix | 401 unbound identity, 403 role/unknown tenant/misrouted DB, 404 disabled production endpoint; malicious tenant header ignored; tenant A source never exposed in B and vice versa |
| Concurrency | Four independent A/B clients contend for 100 units without over-allocation; 4 same-key mixed-payload clients produce one APPLIED, one REPLAYED and 2 CONFLICT; competing 70+70 credit grants produce one grant within 100 approved |
| Fault rollback | SQL trigger throws **after ledger effect insert** for B quota and credit; command, source, right, credit grant and both pool counters remain unchanged; identical command identity can succeed after trigger removal |

One run's exact `TestRunId` is the GitHub Actions `app01a3-<run_id>-<attempt>` file uploaded alongside TRX/Cobertura; record the **PR final HEAD SHA**, run link/conclusion, counts, findings and review resolution in PR #23. Run `scripts/Run-App00A.ps1` on Windows/LocalDB, `Verify-Business.ps1` and frontend/Playwright regression. `TestRunId` describes the synthetic test execution, not an EV/P0 or production business TestRun.

## Pre-review CI evidence (superseded by review fixes; new HEAD must pass)

- Source code HEAD: [`cdf62edf2a727d1a72fc2a013e7da9f443279f96`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/cdf62edf2a727d1a72fc2a013e7da9f443279f96).
- [Windows CI #43796071525](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43796071525): **SUCCESS**, TestRunId `app01a3-43796071525-1`; GitHub Actions temporary merge SHA separately recorded from source HEAD in the TRX artifact.
- .NET 10 build **0 warnings / 0 errors**, xUnit v3 Microsoft Testing Platform + two physical nonce-owned LocalDBs **38/38 PASS**, 0 failed/skipped. Migration replay and fault-injection passed; TRX/Cobertura uploaded ([artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43796071525/artifacts/14194327518)).
- Locked Business Baseline verification PASS, Node 26 setup, frontend/online-first PWA build and dependency tests PASS, Playwright Chromium **5/5 PASS** ([artifact](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43796071525/artifacts/14194113273)).
- npm production audit **0 vulnerabilities**; full build-tree audit retains **4 Low** in the pre-existing `deepmerge` → Rollup/Workbox/PWA dev/build dependency chain; the configured Moderate+ gate PASS. This is **not** a zero-finding full audit and does not authorize production.
- Independent review on `c9d751f` found three P2 and one P3 and requested regression fixes. The new final PR HEAD must pass **42/42** physical tests and browser gates, with its own TestRunId and completed review threads before merge; the 38/38 run above is historical evidence only.

## Remaining scope after APP-01A.3

APP-01B..E still own manifest/transfer, actual delivery/sale, receipt, custody, AR/COGS, invoice and 91/91 AC-03 integrated assertions, plus cross-module race/restore/EV checks. Reviewed/green synthetic CI alone must not be interpreted as production admission or policy approval.
