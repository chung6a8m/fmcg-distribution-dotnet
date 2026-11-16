# FMCG Distribution — .NET DMS Application

**Status:** APP-00A foundation merged with CI review; APP-00B dependencies merged; APP-00B/C browser/PWA acceptance pending Issue #13 (nonproduction/synthetic only). Owner approved APP-00 and APP-01A..E on 2026-11-15 for **synthetic nonproduction only**.

The `business/` snapshot is complete and read-only. Verify its exact tree against `business.lock.json` before changing application code. No fork/submodule/runtime dependency to the SQL or canonical repositories.

## APP-00A — Foundation

- .NET 10 LTS / ASP.NET Core API + Dapper + FluentMigrator; database-per-tenant SQL Server.
- React + TypeScript + Vite + PWA online-first, no offline business writes.
- Two SQL Server LocalDB scratch databases, signed **synthetic-only** JWT dev/test tenant identity, negative cross-tenant routing tests, migration guard.
- No actual A/B/AR/COGS business API yet. That belongs to APP-01A..E.

## APP-00B — toolchain alignment

APP-00B dependencies are merged. Before APP-01A, complete browser/PWA acceptance under the [three-PR APP-00B plan](docs/roadmap/app-00b-dependency-modernization.md): pinned .NET SDK 10.0.404, NuGet security/patch baseline, xUnit v3/Microsoft Testing Platform, Tailwind CSS v4 and reproducible npm lockfile. No changes are authorized in immutable `business/` or `business.lock.json`.

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

**Owner exception OWN-APP00B-13-02:** 125% zoom/display scale is outside APP-00B/C acceptance and not deferred in the roadmap. At below-320px CSS viewports, layout may overflow. This does not remove the 100%/PWA evidence or Owner acceptance gate in [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13); APP-01A remains blocked. No EV/P0 or tenant go-live authorization.
