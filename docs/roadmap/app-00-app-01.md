# Application roadmap — Owner authorized / implementation evidence required

Business source: [read-only Business Baseline](../../business/README.md), [oracle AC-03](../../business/reference-data/expected-results/golden-oracles.json), [owner decisions](../decisions/owner-app-2026-11-15.md).

| Increment | Scope chính | Exit condition |
|---|---|---|
| APP-00A | SQL Server database-per-tenant skeleton, .NET 10 API, Dapper, FluentMigrator, React PWA Online-first, test harness/CI | Migrations fresh/repeat, two actual LocalDB tenants, negative cross-tenant, guard, build + CI PASS on HEAD |
| APP-00B | Toolchain modernization: SDK/NuGet/CI, xUnit v3 + MTP, Tailwind v4 + frontend lockfile | Three separately reviewed PRs; app tests + tenant isolation + Playwright 100% UI/PWA regressions green on final HEAD; 125% excluded by [Owner exception](../decisions/owner-app00b-13-02-125-scale-exception.md); [APP-00B plan](app-00b-dependency-modernization.md) |
| APP-01A | Single integrated source/command ledger, Tenant/Auth boundary, Customer/ShipTo, SKU/Location, Trip, A order/line, B quota/grant | Same command replay; changed payload CONFLICT; tenant A/B access denied; A confirm no AR/COGS |
| APP-01B | T0→T1→T2: A40/B30 manifest/rights and stock transfer; B31 price15 offered verified cash465 | No overlapping A/B rights; B31 `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT` with zero effect/receipt |
| APP-01C | T3 B20 cash200/AR_B100/COGS200; T4 A25/AR_A375/COGS250 | Atomic source/ledger, fault rollback and no double consume |
| APP-01D | T5 cashier accept200; T6 unload A15/B10 | OFFER no money posting; custody/fund paired; **7 checkpoints ×13 fields = 91/91** directly from app persistence |
| APP-01E | 2-client races, retry/rollback, source reconciliation, integrated regression | Evidence TestRunId/actual/diff, reviewed CI HEAD; AC-11 full remains NOT_VERIFIED without C→P, lost ACK/restart |

Use `SYNTHETIC_UNITLESS` and `invoiceEligible=false`. No production B, offline business writes, EV/P0 or tenant go-live.
APP-00B is a prerequisite to APP-01A implementation; do not treat toolchain PASS as business/EV/P0 acceptance. Keep each increment in its own PR, resolve findings and rerun CI before merge.

APP-00B/C browser gate: 100% UI and PWA evidence is **ACCEPTED** under [OWN-APP00B-13-03](../decisions/owner-app00b-13-03-browser-acceptance.md); [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13) is authorized to close. The APP-00B prerequisite no longer blocks APP-01A implementation, which must proceed as a separate reviewed increment under its own exit criteria. 125% remains **EXCEPTION_ACCEPTED / NOT_TESTED**, not a roadmap requirement.
