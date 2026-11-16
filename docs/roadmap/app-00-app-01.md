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
APP-00B was a prerequisite and is **ACCEPTED**; do not treat toolchain PASS as business/EV/P0 acceptance. Keep each increment in its own PR, resolve findings and rerun CI before merge.

APP-00B/C browser gate: 100% UI and PWA evidence is **ACCEPTED** under [OWN-APP00B-13-03](../decisions/owner-app00b-13-03-browser-acceptance.md); [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13) is **CLOSED / completed**. The APP-00B prerequisite was satisfied before APP-01A implementation; APP-01A.1/.2/.3 subsequently passed independent review and synthetic exit criteria (see closeout below). 125% remains **EXCEPTION_ACCEPTED / NOT_TESTED**, not a roadmap requirement.

## APP-01A closeout (2026-11-16; synthetic/nonproduction)

- **APP-01A.1 [#21](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/21): MERGED / SCHEMA_VERIFIED.** Integrated schema, two physically isolated LocalDB tenant migrations, FK/read-write negatives. Reviewed PR-head CI 14/14.
- **APP-01A.2 [#22](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/22): MERGED / COMMANDS_VERIFIED.** Explicit Serializable Dapper commands, shared A/B stock entitlement pools, B credit cap, canonical idempotency. Reviewed PR-head CI 25/25.
- **APP-01A.3 [#23](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/23): MERGED / BEHAVIOR_VERIFIED.** Concurrency, SQL after-effect rollback, synthetic AP-01 approval, logical asOf with per-source-line reconciliation and two-tenant security. Four review findings (3 P2/1 P3) fixed/resolved. Final reviewed PR HEAD `0b5fd0c` [CI #43800609004](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43800609004) **42/42 LocalDB + 5/5 Playwright PASS** with TestRunId `app01a3-43800609004-1`. Merged `main` HEAD `644203c7` [CI #43801711102](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43801711102) **SUCCESS**.
- [Issue #2](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/2) is **CLOSED / completed**. [Closeout, evidence and limitations](../closeout/2026-11-16-app-01a.md). No business baseline change.

**Important:** `SYNTHETIC_VERIFIED` applies to APP-01A's bounded nonproduction slice, **not** 91/91 AC-03, real policy/mode, EV/P0, invoices/accounting or tenant go-live. Stock `StockPool` and `StockRight` remain synthetic opening/entitlement, not physical warehouse/vehicle custody. Preserve 125% Owner display exception as `NOT_TESTED`, not PASS or deferred work.

## APP-01B implementation status — physical state first

- **APP-01B.1: SCHEMA_VERIFIED (PR #25)** — [schema and test contract](app-01b1-physical-stock-schema.md). Migration `202611160004` introduces explicit immutable opening, per-location physical balance, rights-scoped Trip manifest/lines, immutable paired transfer movement and strong tenant/Trip/source FKs. Seven two-tenant LocalDB schema tests; new 49-test CI floor. APP-01A `StockPool` remains an allocation entitlement, not mutable physical stock.
- **APP-01B.2: NOT_IMPLEMENTED** — Serializable Dapper T1 stock/cost transfer and B31 quota/cash-offer preflight with durable no-effect rejection.
- **APP-01B.3: NOT_RUN** — oracle comparison **39/39 T0–T2** with actual SQL persistence, rollback/idempotency/concurrency/security/CI evidence.

[APP-01B executable proposal](app-01b-manifest-transfer-proposal.md): T1 expected MAIN100→MAIN30/TRIP70 (A40/B30), T2 B31 rejection leaves stock/cost/cash/AR/COGS unchanged. The 39/39 target and full 91/91 AC-03 are **NOT_VERIFIED**; APP-01C..E remain pending. APP-01B.1 passed independent schema review (including loaded-line immutability regressions) and exact-HEAD CI on PR #25; this does not verify APP-01B's 39/39 oracle.
