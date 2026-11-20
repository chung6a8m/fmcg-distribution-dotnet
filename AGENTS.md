# Agent guidance — DMS application

- Business-first, solo developer, small PRs. `business/` and `business.lock.json` are immutable locked copies; never change them to pass implementation tests.
- All tenant routing comes from server-validated identity, never request-selected connection string.
- APP-00A uses LocalDB synthetic scratch only. Protect all DROP/reset/migration targets by instance, database name, ownership marker and nonce.
- .NET 10 / ASP.NET Core / Dapper / FluentMigrator, React / TS / Vite / PWA online-first; no business writes while offline.
- APP-00B is a prerequisite to APP-01A: pin .NET 10.0.404, xUnit v3 + Microsoft Testing Platform v2 (`dotnet test --solution FmcgDms.slnx -- --report-trx`), Node 26 LTS and Tailwind v4. Every PR must keep tests/evidence green.
- APP-01A..E implements one integrated source and ledger model. The SQL repository's `ac03/ac09/ac21/ac06` schemas are executable references, not app migrations.
- Separate Owner decisions, SQL evidence, APP implementation verification, EV/P0 and tenant go-live. Never claim unsupported PASS.
- Before UI design or implementation work, read `docs/ui-ux/README.md` and its decision/status ledger. Distinguish proposed design defaults from adopted requirements; use the screen/component templates and source-linked UX patterns. This documentation does not authorize runtime work, override business contracts, or reopen the Owner's scoped 125% exception.
