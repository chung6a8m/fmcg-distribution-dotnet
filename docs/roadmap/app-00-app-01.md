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

## APP-01B closeout — synthetic / nonproduction (2026-11-16)

**Verdict on completion of PR #27 merge gate: APP-01B SYNTHETIC_VERIFIED.** Independent review, final exact-HEAD CI and outcome are linked from [PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27). The [APP-01B closeout](../closeout/2026-11-16-app-01b.md) is the scope/evidence index. Do not use the earlier CI on a superseded HEAD as final approval.

- **APP-01B.1: MERGED / SCHEMA_VERIFIED ([PR #25](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/25)).** [Physical stock migration and schema tests](app-01b1-physical-stock-schema.md); 49-test CI floor, two nonce-created LocalDB tenants, immutable opening/movement and strong tenant/Trip/source FKs.
- **APP-01B.2: MERGED / COMMANDS_VERIFIED ([PR #26](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/26)).** [Atomic Dapper LoadManifest and B31 rejection](app-01b2-manifest-commands.md); 62-test floor, concurrent double-load, injected rollback/retry, rights/actor/tenant negatives. Physical custody and cost move together; a rejected B31 is non-posting.
- **APP-01B.3: SQL_ORACLE_VERIFIED subject to reviewed final-HEAD CI ([PR #27](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/27)).** [SQL-backed T0–T2 oracle contract](app-01b3-sql-oracle.md); 64-test floor, **39/39 per tenant / 78/78 across two databases**, actual/diff reports, immutable baseline hash, mutation sentinels, full-trip ledger fingerprint and rejected-command source absence. The final tested HEAD and artifact URLs are recorded on PR #27.

T0 MAIN 100/1000, TRIP 0/0 and A40/B30 -> T1 MAIN 30/300, TRIP 70/700 -> T2 B31 at 15/cash 465 yields durable `REJECTED_QUOTA / B_QUOTA_INSUFFICIENT` with no new source/ledger/physical/financial effects. The original [APP-01B proposal](app-01b-manifest-transfer-proposal.md) remains the executable boundary.

**Out of scope:** Full AC-03 **91/91** through T3–T6, APP-01C–E, real B/offline, invoices, policy, EV/P0 and go-live are **NOT_VERIFIED / NOT_AUTHORIZED**. The 125% display exception remains Owner-accepted **NOT_TESTED**. Next vertical slice is **APP-01C** (T3 B20 and T4 A25 postings), independently gated.
