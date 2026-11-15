# Agent guidance — DMS application

- Business-first, solo developer, small PRs. `business/` and `business.lock.json` are immutable locked copies; never change them to pass implementation tests.
- All tenant routing comes from server-validated identity, never request-selected connection string.
- APP-00A uses LocalDB synthetic scratch only. Protect all DROP/reset/migration targets by instance, database name, ownership marker and nonce.
- .NET 10 / ASP.NET Core / Dapper / FluentMigrator, React / TS / Vite / PWA online-first; no business writes while offline.
- APP-01A..E implements one integrated source and ledger model. The SQL repository's `ac03/ac09/ac21/ac06` schemas are executable references, not app migrations.
- Separate Owner decisions, SQL evidence, APP implementation verification, EV/P0 and tenant go-live. Never claim unsupported PASS.
