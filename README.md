# FMCG Distribution — .NET DMS Application

**Status (2026-11-16):** APP-00A/APP-00B complete, APP-01A.1 schema [PR #21](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/21) and APP-01A.2 commands [PR #22](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/22) merged/CI verified. APP-01A.3 approval, as-of audit and two-tenant concurrency/security are implemented in **draft [PR #23](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/23), implementation CI verified and independent review pending**. **APP-01A overall NOT_VERIFIED** and full AC-03 business flow not implemented. Owner authorization remains **synthetic nonproduction only**.

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
- Routes `/api/synthetic/app01a/commands/*` exist **only** when signed synthetic auth is enabled in Development/IntegrationTest and require nonce-owned, bound LocalDB. See [detailed contract](docs/roadmap/app-01a2-command-transactions.md). APP-01A.3 code-head CI verified in draft PR #23, independent review pending; APP-01A overall is NOT_VERIFIED.

## APP-01A.3 — Audit, approval and two-tenant verification (draft PR #23)

- Adds synthetic AP-01 credit-policy evidence with maker/checker separation, versioned scope, expiry, explicit cap and source-linked approval ID. No approval, ambiguous approval or missing policy is fail-closed; **real credit policies and thresholds are still UNSET**.
- Synthetic JWT is actor-bound and command routes have Warehouse/Sales permission separation. Read-only auditor endpoint `GET /api/synthetic/app01a/audit/as-of?asOf=<UTC>` provides logical as-of actor/source/effect/pending, approval lineage and A/B rights/effects reconciliation; **not temporal SQL history, production audit or accounting**.
- Adds 13 physical LocalDB verification cases for concurrent clients, mixed-payload replay, effect-trigger rollback, approval negatives, two-tenant HTTP authorization and disabled production surface. Requires reviewed final-HEAD CI and TestRunId before APP-01A acceptance. See [APP-01A.3 verification contract](docs/roadmap/app-01a3-audit-policy-verification.md).

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

**Definition of Done APP-00A:** build + test + migrations repeated safely on fresh tenant A/B LocalDB, negative cross-tenant read/write, strict scratch reset guard, PWA build, business validator and PR-head CI PASS. APP-01A should follow in another PR after review.

**No production/pilot/EV/P0 authorization** follows from this application foundation.

## Frontend Playwright regression (synthetic only)

From `web/` on Node 26 LTS, run `npm ci --no-audit --no-fund`, `npm run build`, `npx playwright install chromium`, then `npm run test:e2e`. The browser suite starts a production `vite preview` and checks API live/fallback states, **100%** layout at desktop and narrow CSS viewport, and real service-worker V1→V2 behavior (the V2 release is synthesized from the built PWA in a temporary directory, served under the same origin; no tracked source files are rewritten). CI uploads `app00b-playwright-evidence` with reports, screenshots and traces.

**Owner exception OWN-APP00B-13-02:** 125% zoom/display scale is outside APP-00B/C acceptance and not deferred in the roadmap. At below-320px CSS viewports, layout may overflow. The 100% Edge/PWA evidence was **accepted** under OWN-APP00B-13-03 and [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13) is closed; APP-01A implementation is unblocked, but its own business exit criteria are still pending. No EV/P0 or tenant go-live authorization.
