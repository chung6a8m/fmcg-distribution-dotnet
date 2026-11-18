# APP-02B.6 — Bounded source-only merge closeout (18/11/2026)

**Disposition:** `MERGED / B6_GITHUB_HOSTED_CI_TESTED / KNOWN_HIGH_SECURITY_DEBT_59_OPEN / REVOCATION_53_OPEN / CASE_INTEGRATION_67_OPEN / PHYSICAL_PREPROD_NOT_VERIFIED`.

This is a **source integration closeout**, **not** `POLICY_GOVERNANCE_VERIFIED`, `PREPROD_FOUNDATION_VERIFIED`, an APP-00A full-CI PASS, or an approval to apply the migration, enable API/Worker business logic or deploy. APP-02C/D/E, production and pilot remain **NOT_AUTHORIZED**.

## Source, Owner approval and independent review

| Field | Verified value |
|---|---|
| Repository / PR | [`chung6a8m/fmcg-distribution-dotnet` PR #66](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/66) — **MERGED** |
| Final reviewed PR source HEAD | [`93f1f1610ac89bb5f5ddfbfe828ea2d06f51e07d`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/93f1f1610ac89bb5f5ddfbfe828ea2d06f51e07d) |
| Squash merge commit on `main` | [`fec324797647599c0f25bc5645f5351c0e5f2b45`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/fec324797647599c0f25bc5645f5351c0e5f2b45) |
| Owner decision | [`OWN-APP02B6-DEBT-01`](../decisions/owner-app02b6-bounded-source-only-merge-2026-11-18.md): explicit B.6-specific **source-only** merge acceptance, not an inherited B.5 exception |
| Reviewed code HEAD / findings | `a7ce689dffefe577b465519ca62f078465696a6e`; four source findings corrected (CASE-scoped L2, source revision replay, PR-source checkout, measured SQL peer after-hashes) |
| Final independent source review | [Review #5954222414](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/66#pullrequestreview-5954222414) |
| Postmerge verification | [PR #66 source-only merge closeout comment](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/66#issuecomment-6975059662); GitHub reports `merged=true`, `main` points to the squash SHA |

Final PR diff: 14 files, strictly standalone B.6 policy source, SQL migration, test projects/workflow, operator runbook and docs. No changes to `business/`, `business.lock.json`, existing PREPROD API/Worker registration, `web/`, production configuration or tenant business routes. Policy L0 starts empty; there are no business policy defaults, SQL role assignments or automatic startup migrations.

## Exact final-source CI and artifacts

| Check | Exact-source evidence | Disposition |
|---|---|---|
| APP-02B.6 source + SQL | [Workflow #44278544466](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544466), Ubuntu source/unit **4/4**, Windows disposable two-tenant SQL LocalDB **2/2**, **0 failed/skipped** | **SUCCESS 2/2 jobs**, `GITHUB_HOSTED_LOCALDB_ONLY` |
| B.2 ephemeral runtime | [Workflow #44278544395](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544395) | **SUCCESS**, not a physical PREPROD host |
| B.3 identity | [Workflow #44278544443](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544443) | **SUCCESS**, GitHub-hosted only |
| B.4 SQL registry | [Workflow #44278544409](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544409) | **SUCCESS**, GitHub-hosted only |
| B.4 bounded web/PWA/Playwright | [Workflow #44278544365](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544365) | **SUCCESS**, not a replacement for full APP-00A |
| B.5 authorization | [Workflow #44278544382](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544382) | **SUCCESS**, source-only |
| Mandatory APP-00A | [Workflow #44278544425](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44278544425) | **FAILED**, full `npm audit --audit-level=moderate` reports **11 HIGH `picomatch`** (#59); later browser stages **SKIPPED** |

APP-00A **pre-audit .NET/SQL**: 296/296 tests PASS, 0 failed/skipped; APP-02A.4 24/24 tenant-group instances, 60/60 SQL checkpoints, 1,440/1,440 expected/actual/diff cells and 5/5 mutation checks PASS. Production-only npm audit: 0 vulnerabilities. None of these results reverses the mandatory **full-tree audit FAIL**.

**B.6 source provenance** (both artifacts verified from the final PR HEAD `93f1f1610ac89bb5f5ddfbfe828ea2d06f51e07d`):

- Source artifact `app02b6-source-ci-only`, ID `14416580551`; ZIP SHA-256 `ef3305c660f5edec227e45bcaf8a710b0eceb34026c8efb4f0c5192cce059474`; `TestRunId=app02b6-source-44278544466-1`.
- SQL artifact `app02b6-localdb-sql-ci-only`, ID `14416066200`; ZIP SHA-256 `3c08defc12b5f953df6c93da9cc4b6987cca4950993f8a5215c1b2b993fc4b1c`; `TestRunId=b6-localdb-44278544466-1`.
- Migration SQL SHA-256 `AAEC88AF02CB0CEFCCCB52D1172EBD4C2C51397BBB5BA841FF09B0080FD467FA` for `B6_20261118_001`. JSON witnesses, TRX counts, source SHA, separate CASE checks and independently measured before/after physical LocalDB tenant peer fingerprints were inspected. No independent real PREPROD SQL/DBA acceptance was performed.

## Open debt and strictly held boundaries

| Tracker | Pending acceptance — do not treat source merge as closing |
|---|---|
| [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53) | Restore-safe, external monotonic revocation high-watermark across B.3/B.4/B.5; Worker/CASE apply-time fencing |
| [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) | **11 HIGH** full-tree npm audit vulnerabilities; mandatory APP-00A gate remains **FAILED**, not suppressed |
| [#67](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/67) | Real B.3 actor/session ↔ B.4 tenant registry ↔ B.5 current epoch/permissions ↔ B.6 policy/APP-02A immutable CASE source integration |
| [#68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68) | Intermittent A11-09 tenant-B OS-restart HTTP 500 root cause **unresolved**, despite final-source 296/296 green test |
| B.7/B.8/B.9 | PITR/restore, independent immutable audit and complete PREPROD acceptance **NOT_VERIFIED** |
| G-B01..G-B05 / 30 B.1 cases | Physical PREPROD two-tenant, SQL/Vault/DBA, security/backup/audit/oracle witnesses **NOT_RUN / NOT_VERIFIED** |
| Production P0 and actual EV-08/AC-11 | `G-STOCK`, `G-CUT`, `G-IDEMP`, `G-LEDGER`, `G-INV` and real-environment effects **NOT_VERIFIED** |

**Never** infer source merge authorizes running `B6_20261118_001` on real SQL, provisioning real L0 values/operators, deploying policies, admitting business transactions, production, Pilot or APP-02C/D/E.

## Documentation handoff

This docs-only closeout PR replaces **current B.6 status** in README, [APP-02 roadmap](../roadmap/app-02.md), [APP-02B foundation roadmap](../roadmap/app-02b-preprod-foundation.md) and [B.6 contract](../roadmap/app-02b6-policy-registry-approvals.md) with `MERGED / B6_GITHUB_HOSTED_CI_TESTED / PHYSICAL_PREPROD_NOT_VERIFIED`. Historical pre-merge evidence remains traceable through PR #66 and its reviews; this document does **not** promote any real-environment gate.
