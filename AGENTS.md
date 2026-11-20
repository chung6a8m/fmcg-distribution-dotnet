# Agent guidance — DMS application

- Business-first, solo developer, small PRs. `business/` and `business.lock.json` are immutable locked copies; never change them to pass implementation tests.
- All tenant routing comes from server-validated identity, never request-selected connection string.
- APP-00A uses LocalDB synthetic scratch only. Protect all DROP/reset/migration targets by instance, database name, ownership marker and nonce.
- .NET 10 / ASP.NET Core / Dapper / FluentMigrator, React / TS / Vite / PWA online-first; no business writes while offline.
- APP-00B is a prerequisite to APP-01A: pin .NET 10.0.404, xUnit v3 + Microsoft Testing Platform v2 (`dotnet test --solution FmcgDms.slnx -- --report-trx`), Node 26 LTS and Tailwind v4. Every PR must keep tests/evidence green.
- APP-01A..E implements one integrated source and ledger model. The SQL repository's `ac03/ac09/ac21/ac06` schemas are executable references, not app migrations.
- Separate Owner decisions, SQL evidence, APP implementation verification, EV/P0 and tenant go-live. Never claim unsupported PASS.
- Before UI design or implementation, read `docs/ui-ux/README.md`, `docs/ui-ux/decisions.md` and `docs/decisions/owner-uiux-2026-11-20.md`. The 9/9 v0.1 design baseline is `ADOPTED` via PR #79. UI-01 foundations/demo controls are `MERGED / CI_VERIFIED` (synthetic/nonproduction) via PR #80; see `docs/closeout/2026-11-20-ui-01.md` for source CI and limits. Reuse the shared theme, tokens, fonts and controls. For source-bound acceptance and platform limits see `docs/ui-ux/ui-01-verification.md`. UI-02..05 remain `NOT_AUTHORIZED`. Preserve business contracts and the scoped 125% APP-00B/C exception.
