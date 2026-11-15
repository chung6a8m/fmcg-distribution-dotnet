# FMCG Distribution — .NET DMS Application

**Status:** APP-00A implementation in progress, CI/review required. Owner approved APP-00 and APP-01A..E on 2026-11-15 for **synthetic nonproduction only**.

The `business/` snapshot is complete and read-only. Verify its exact tree against `business.lock.json` before changing application code. No fork/submodule/runtime dependency to the SQL or canonical repositories.

## APP-00A — Foundation

- .NET 10 LTS / ASP.NET Core API + Dapper + FluentMigrator; database-per-tenant SQL Server.
- React + TypeScript + Vite + PWA online-first, no offline business writes.
- Two SQL Server LocalDB scratch databases, signed **synthetic-only** JWT dev/test tenant identity, negative cross-tenant routing tests, migration guard.
- No actual A/B/AR/COGS business API yet. That belongs to APP-01A..E.

## Windows local checks

Requires .NET 10 SDK, Node 22+, PowerShell and SQL Server 2019+ LocalDB. Do not aim at production SQL.

```powershell
sqllocaldb start MSSQLLocalDB
.\\scripts\\Run-App00A.ps1   # checked-in business tree + clean working tree, model validator,
                           # .NET restore/build/LocalDB tests and npm test/build
```

The local verification refuses staged, unstaged or untracked files inside `business/`; commit-free local checks cannot accidentally accept a modified baseline. CI runs the frontend health contract regression tests, including the HTTP 200 SPA fallback case. PWA updates use a visible user-confirmed prompt (`registerSW`/`onNeedRefresh`); to exercise the update interaction manually, serve a production build, leave its tab open, rebuild/redeploy changed frontend assets and revisit the tab.

To run only the public liveness probe: `dotnet run --project src/FmcgDms.Api --urls http://localhost:5116` then `GET /health/live`. Synthetic-authenticated diagnostic endpoints are **not enabled by default** and **not production auth**. No secrets are committed; synthetic JWT test keys are only in the integration test project.

**Definition of Done APP-00A:** build + test + migrations repeated safely on fresh tenant A/B LocalDB, negative cross-tenant read/write, strict scratch reset guard, PWA build, business validator and PR-head CI PASS. APP-01A should follow in another PR after review.

**No production/pilot/EV/P0 authorization** follows from this application foundation.
