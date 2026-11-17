# FMCG Distribution — .NET DMS Application

**Status (2026-11-17):** APP-00A/B and APP-01A–D, APP-01E.1 [#33](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/33), E.2 [#34](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/34) and **E.3 [#35](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/35)** are **MERGED / SYNTHETIC_VERIFIED** within their individual bounded scopes. E.3 was independently reviewed on exact source HEAD `430850e0`, [Windows Actions #43962422877 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43962422877) **209/209 PASS**, 84/84 rollback/retry cases and **5,796/5,796** SQL cells; merged main `d94f90e`. **E.4 [lost ACK, read-only outcome lookup, genuine TCP/OS process restart](docs/roadmap/app-01e4-lost-ack-restart.md) is IMPLEMENTED / INDEPENDENT_REVIEW_AND_EXACT_HEAD_CI_PENDING** (242-test target, 10 real process reports/460 SQL cells). E.5, full AC-11/C→P, EV/P0, production/pilot and real payments/invoices remain **NOT_VERIFIED / NOT_AUTHORIZED**. See the [roadmap](docs/roadmap/app-00-app-01.md).

The `business/` snapshot is complete and read-only. Verify its exact tree against `business.lock.json` before changing application code. No fork/submodule/runtime dependency to the SQL or canonical repositories.

## APP-00A — Foundation

- .NET 10 LTS / ASP.NET Core API + Dapper + FluentMigrator; database-per-tenant SQL Server.
- React + TypeScript + Vite + PWA online-first, no offline business writes.
- Two SQL Server LocalDB scratch databases, signed **synthetic-only** JWT dev/test tenant identity, negative cross-tenant routing tests, migration guard.
- No production A/B business API or AR/COGS booking yet. APP-01A.2 introduces signed synthetic LocalDB-only command endpoints.

## APP-00B — toolchain alignment

APP-00B dependencies and **100% browser/PWA acceptance are complete** ([Owner decision](docs/decisions/owner-app00b-13-03-browser-acceptance.md), [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13)). Tooling: .NET SDK 10.0.404, xUnit v3/MTP v2, Tailwind CSS v4, locked Node 26 LTS frontend. Historical Node 24 decisions are superseded for current runs. The APP-00B prerequisite no longer blocks APP-01A. `business/` and `business.lock.json` remain immutable.

## APP-01A.1 — Integrated schema (schema-only increment)

- Adds an integrated, **per-tenant** Customer/ShipTo/Sku/StockLocation/Trip, A order/line, source/command/effect ledger, shared A/B StockRight, AReservation, BQuota and BCreditGrant schema.
- Foreign keys bind every root entity to `AppTenantBinding`; composite FKs enforce Customer+ShipTo identity and same-tenant source/rights; SQL checks and unique keys protect per-row amounts and duplicate source/effect identities.
- Introduces 5 SQL Server LocalDB integration tests for two database migrations/replay, isolation/invalid tenant writes, Customer/ShipTo mismatch, A/B right kind/source uniqueness, and command/ledger duplicate protection.
- **APP-01A.1 scope was schema-only**; Dapper command transactions and aggregate allocation were subsequently implemented in APP-01A.2. Real AR/COGS and delivery remain APP-01B–E. See [APP-01A.1 design/acceptance](docs/roadmap/app-01a1-integrated-schema.md).

## APP-01A.2 — Transactional A/B rights (synthetic only)

- Explicit Dapper SERIALIZABLE transactions implement `ReserveAStock`, `ConfirmAOrder`, `GrantBQuota` and `GrantBCredit` in one common source/command ledger.
- Shared warehouse/SKU `StockPool` prevents A+B allocations beyond eligible synthetic opening stock; `BCreditPool` requires explicitly seeded synthetic credit capacity. No implicit stock or real credit approval, physical stock movement, delivery or AR/COGS.
- SHA-256 canonical payload v1 and `(tenant, producer, command)` idempotency. Same payload replays the persisted outcome; changed payload returns 409 CONFLICT with zero new effects.
- Routes `/api/synthetic/app01a/commands/*` exist **only** when signed synthetic auth is enabled in Development/IntegrationTest and require nonce-owned, bound LocalDB. See [detailed contract](docs/roadmap/app-01a2-command-transactions.md). APP-01A.3 is now reviewed/merged; the *synthetic APP-01A increment* is verified. APP-01B..E and full AC-03 remain unverified.

## APP-01A.3 — Audit, approval and two-tenant verification (merged PR #23)

**Review closeout:** 3 P2 + 1 P3 findings fixed, all four threads resolved; second review on final PR HEAD `0b5fd0c` found no new blockers. This is synthetic implementation verification only.

- Adds synthetic AP-01 credit-policy evidence with maker/checker separation, versioned scope, expiry, explicit cap and source-linked approval ID. No approval, ambiguous approval or missing policy is fail-closed; **real credit policies and thresholds are still UNSET**.
- Synthetic JWT is actor-bound and command routes have Warehouse/Sales permission separation. Read-only auditor endpoint `GET /api/synthetic/app01a/audit/as-of?asOf=<UTC>` provides logical as-of actor/source/effect/pending, approval lineage and A/B rights/effects reconciliation; **not temporal SQL history, production audit or accounting**.
- Adds 17 physical LocalDB verification cases (including review-fix regressions) for concurrent clients, mixed-payload replay, effect-trigger rollback, approval negatives, two-tenant HTTP authorization and disabled production surface. Reviewed final-HEAD CI and TestRunId are recorded in [APP-01A closeout](docs/closeout/2026-11-16-app-01a.md). See [APP-01A.3 verification contract](docs/roadmap/app-01a3-audit-policy-verification.md).

## APP-01B.1 — Physical stock schema (schema verified)

Migration `202611160004` adds `PhysicalStockOpening` (immutable T0), `PhysicalStockBalance` (per-location physical quantity/cost), `TripManifest`/`TripManifestLine` (A/B-rights-bound draft/load state) and immutable `PhysicalStockMovement` (one source-linked paired warehouse→vehicle transfer record per line). Stock rights/`StockPool` remain synthetic **allocation** counters, not physical stock. New tests target **49/49 LocalDB minimum** across nonce-owned two tenant databases, with source/Trip/warehouse/vehicle/decimal/immutability negatives. See [APP-01B.1 design and verification](docs/roadmap/app-01b1-physical-stock-schema.md).

**APP-01B.1 is schema-only.** APP-01B.2 now implements the synthetic Dapper T1 load and B31 preflight (PR #26; exact-head CI/review evidence tracked on PR); APP-01B.3 verifies the immutable 39/39 oracle. This bounded T0–T2 oracle was independently verified on the reviewed APP-01B HEAD; it does not imply full T0–T6 AC-03, production B/real cash, invoice, policy or go-live authority. Keep `business/` and `business.lock.json` immutable.

## APP-01B.2 — Manifest load and quota preflight (synthetic implementation; PR #26)

- `POST /api/synthetic/app01b/commands/load-manifest` (warehouse role) loads every pre-staged A/B right in one explicit Serializable SQL transaction; debits warehouse and credits vehicle for exact decimal quantities and carried cost, persists immutable source lines/movements and balanced nonfinancial custody ledger effects. Rights and StockPool remain allocation entitlements, not sales/COGS.
- `POST /api/synthetic/app01b/commands/preflight-b-sale` (sales role) is deliberately non-posting: B31 at price15 with synthetic offer465 returns durable `REJECTED / REJECTED_QUOTA / B_QUOTA_INSUFFICIENT`; the preflight itself has no receipt/cash/AR/invoice/COGS effects, even though distinct synthetic APP-01C.2 B20 posting is now implemented.
- Reuses APP-01A canonical command identities, hash/replay/conflict, JWT tenant boundary and nonce-owned LocalDB. Adds thirteen real LocalDB tests (62-test CI floor) for paired transfer, B31 no-effect, contention, rollback, residual decimal cost and two tenants. [APP-01B.2 contract](docs/roadmap/app-01b2-manifest-commands.md). **Exact-head CI/review evidence is tracked in PR #26; the later APP-01B.3 39/39 per tenant was independently verified in PR #27.**

## APP-01C.2 and APP-01C.3 — Source-bound synthetic T3 B20 / T4 A25

- [APP-01C.2](docs/roadmap/app-01c2-b20-atomic-posting.md) B20: one Dapper `Serializable` transaction posts exact loaded B stock issue, verified cash witness, receipt allocation, AR/COGS, driver custody, B right and approved credit-grant consumption. PR #29 merged; no real cash verification/policy or production authorization.
- [APP-01C.3](docs/roadmap/app-01c3-a25-atomic-posting.md) A25: independently approved synthetic N-day terms and price are frozen in an immutable source-bound snapshot **at Confirm**. `POST /api/synthetic/app01c/commands/post-a-partial-delivery` accepts only source/order/right/quantity references, never a new price or due date. A25 moves only the delivered quantity/cost, creates due-dated AR and four source-linked ledger effects; no receipt, B credit or custody. A/A and A/B postings share Trip lock. On branch for independent review and exact-HEAD CI; do not claim verified before green CI.
- Both posting APIs exist **only** under signed synthetic auth in Development/IntegrationTest with nonce-owned two-tenant LocalDB, and are not production routes. New A25 CI target **93 tests** while preserving the independent APP-01B.3 39/39 SQL oracle per tenant; future APP-01C.4 must separately establish **65/65 T0–T4 per tenant**, not full 91/91. Business Baseline remains locked.

## Windows local checks

For a Windows machine managed by nvm-windows, run `nvm install 26.13.1` and `nvm use 26.13.1` in PowerShell, then check `node --version` (expect `v26.x`). If Node was installed directly instead, use the official Node 26 LTS installer from https://nodejs.org/en/download. Do not mix the two installation methods. The `web/.nvmrc` file selects the Node 26 release line for compatible version managers.

Requires .NET 10 SDK 10.0.404, Node 26 LTS (>=26.13.1), PowerShell and SQL Server 2019+ LocalDB. Do not aim at production SQL.

```powershell
sqllocaldb start MSSQLLocalDB
.\scripts\Run-App00A.ps1   # checked-in business tree + clean working tree, model validator,
                           # .NET restore/build/LocalDB tests and npm test/build
```

The local verification refuses staged, unstaged or untracked files inside `business/`; commit-free local checks cannot accidentally accept a modified baseline. CI runs the frontend health contract regression tests, including the HTTP 200 SPA fallback case. PWA updates use a visible user-confirmed prompt (`registerSW`/`onNeedRefresh`); to exercise the update interaction manually, serve a production build, leave its tab open, rebuild/redeploy changed frontend assets and revisit the tab.

To run only the public liveness probe: `dotnet run --project src/FmcgDms.Api --urls http://localhost:5116` then `GET /health/live`. Synthetic-authenticated diagnostic endpoints are **not enabled by default** and **not production auth**. No secrets are committed; synthetic JWT test keys are only in the integration test project.

**Definition of Done APP-00A:** build + test + migrations repeated safely on fresh tenant A/B LocalDB, negative cross-tenant read/write, strict scratch reset guard, PWA build, business validator and PR-head CI PASS. APP-01A.1/.2/.3 were subsequently implemented and reviewed.

**No production/pilot/EV/P0 authorization** follows from this application foundation.

## Frontend Playwright regression (synthetic only)

From `web/` on Node 26 LTS, run `npm ci --no-audit --no-fund`, `npm run build`, `npx playwright install chromium`, then `npm run test:e2e`. The browser suite starts a production `vite preview` and checks API live/fallback states, **100%** layout at desktop and narrow CSS viewport, and real service-worker V1→V2 behavior (the V2 release is synthesized from the built PWA in a temporary directory, served under the same origin; no tracked source files are rewritten). CI uploads `app00b-playwright-evidence` with reports, screenshots and traces.

**Owner exception OWN-APP00B-13-02:** 125% zoom/display scale is outside APP-00B/C acceptance and not deferred in the roadmap. At below-320px CSS viewports, layout may overflow. The 100% Edge/PWA evidence was **accepted** under OWN-APP00B-13-03 and [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13) is closed; APP-01A synthetic exit criteria were subsequently reviewed, verified and closed in Issue #2; the full AC-03 business gates are still pending. No EV/P0 or tenant go-live authorization.
