# APP-01A — Integrated model handoff (CLOSED / SYNTHETIC_VERIFIED)

**Closeout 2026-11-16:** [Issue #2](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/2) is **CLOSED / completed**. All increments were independently reviewed and merged: [PR #21](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/21), [PR #22](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/22) and [PR #23](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/23). Reviewed PR HEAD `0b5fd0c`: [CI #43800609004](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43800609004) **SUCCESS**, 42/42 physical two-tenant LocalDB tests, 5/5 Chromium Playwright, TestRunId `app01a3-43800609004-1`. Merged `main` `644203c7`: [CI #43801711102](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43801711102) **SUCCESS**, evidence artifacts uploaded. Four original PR #23 review findings fixed and resolved; round 2 found no new blockers. See [APP-01A closeout and exceptions](closeout/2026-11-16-app-01a.md).

**Scope of SYNTHETIC_VERIFIED:** two nonce-owned SQL Server LocalDB tenants and synthetic Development/IntegrationTest endpoints, not 91/91 AC-03 app data, EV/P0, B production, real business approval or tenant go-live.

## Delivered increments

1. **APP-01A.1 — integrated schema:** [contract](roadmap/app-01a1-integrated-schema.md). Customer/ShipTo/SKU/Location/Trip, AOrder/Line, BusinessCommand/Source/Line/Effect, shared StockRight, AReservation, BQuota and BCreditGrant. Tenant-bound/composite FK, rowversion, physical LocalDB fresh/replay and negatives.
2. **APP-01A.2 — explicit transactions:** [contract](roadmap/app-01a2-command-transactions.md). Dapper Serializable `ReserveAStock`, `ConfirmAOrder`, `GrantBQuota`, `GrantBCredit`; synthetic A/B stock and credit pools; deterministic payload hash version, replay/conflict, no AR/COGS on confirmation.
3. **APP-01A.3 — hardening:** [contract](roadmap/app-01a3-audit-policy-verification.md). Four-client races, SQL after-effect rollback and retry, checked synthetic AP-01 credit approvals, logical asOf with per-source-line reconciliation, two-tenant signed actor/role isolation matrix.

## Non-goals and risks retained

- `business/` and `business.lock.json` remain immutable. SQL scratch schema is evidence, not copied app persistence.
- Stock rights/allocations are entitlements, not actual stock transfer. The logical `asOf` report is not SQL temporal/commit-time accounting history.
- AP-01 approval is seeded synthetic evidence, not an approved live credit policy. Real seller/mode admission, Trip Writer assignment, price/tax/legal invoices, delivery, receipt/custody/AR/COGS and EV/P0 remain unimplemented.
- Owner 125% display exception is **EXCEPTION_ACCEPTED / NOT_TESTED**, not PASS or roadmap debt. The full build/dev dependency audit retains four Low npm advisories; production tree and configured Moderate+ gates passed.

## Handoff

Next implement **APP-01B — T0→T2 manifest and stock movement** under [proposed executable contract](roadmap/app-01b-manifest-transfer-proposal.md): A40/B30 rights; load 70 from MAIN to TRIP without sale/AR/COGS, then reject B31 against quota30 without any physical/cash/receipt effect. APP-01C, D, E later own delivery, receivables, handover/unload and remaining 91/91 AC-03 + cross-module durability. Never modify golden oracle to match application output.
