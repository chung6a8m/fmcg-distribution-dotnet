# OWN-APP02B6-DEBT-01 — Bounded source-only merge approval (APP-02B.6)

**Owner disposition (2026-11-18, Asia/Bangkok):** `OWNER_APPROVED_B6_SOURCE_ONLY_MERGE / CONDITIONAL_SOURCE_ACCEPTANCE`. The Owner explicitly stated: **“Tôi chấp nhận merge ở phạm vi source-only”** in direct response to the independent PR #66 review and its unresolved gates. This approval is **specific to [PR #66](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/66)**; it does not transfer from or to another APP-02B increment. It authorizes **the timing of inert source integration only**, not a finding that its failed or unexecuted gates passed.

## 1. What the Owner accepts

- Merge the **operator-only/inert** B.6 source package, operator-applied (not automatically executed) SQL migration, C# policy contracts, CI tests/workflow and supporting docs into `main`, after exact-final-HEAD independent review and bounded compensating CI checks below.
- Preserve the **empty L0** production/business policy registry (no default price/credit/terms thresholds), no tenant grants/actor principal provisioning, and no registration in PREPROD API/Worker, business endpoints, outbox, CASE resume or startup migration.
- Accept the integration-timing risk while the **mandatory full APP-00A npm audit is explicitly FAILED** with **11 HIGH vulnerabilities** (#59), not PASS. Do not suppress, downgrade, edit, bypass or remove the audit. Production-only audit zero vulnerabilities and bounded web/PWA/Playwright workflow success **do not** replace the full APP-00A failure or its skipped downstream jobs. This is not permission to build/deploy vulnerable runtime artifacts.
- Defer independently witnessed live actor/CASE/source/epoch integration and physical security acceptance; **#53, #59, #67, #68 remain OPEN**, not resolved by merge. A11-09 transient tenant-B HTTP 500 (issue #68) has no established cause even if a subsequent complete APP-00A .NET/SQL run passes.

## 2. Non-negotiable source-only merge gates

1. **SRC-B6-01 — Protected diff boundary:** Independently review the full final diff and B.1 threats/acceptance requirements. Zero unresolved B.6 *source-level* findings with immediate inert-path effect. No `business/`, `business.lock.json`, existing API/Worker business runtime, `web/`, startup migration, secret, production config, default L0, policy grant or live merchant/provider changes.
2. **SRC-B6-02 — B.6 exact-final-HEAD CI:** Source/unit job and Windows disposable SQL LocalDB job PASS (0 failed/skipped required tests); verify exact source SHA checkout, TestRunId, SQL migration SHA, immutable L0/L1/L2/CASE maker-checker, explicit SET/0 vs UNSET, approval/replay/overlap/tenant/CASE isolation, no-write peer before/after hashes and artifact SHA.
3. **SRC-B6-03 — Retained regressions:** B.2, B.3, B.4 SQL, B.4 bounded Windows web/PWA/Playwright and B.5 CI PASS on the same final HEAD. APP-00A .NET/SQL/AC-11/A11-09 and production-only npm audit must pass. **Only the already identified #59 full-tree security-audit failure and its consequent downstream skip are conditionally accepted as merge-timing debt**; label APP-00A `FAILED`, not `PASS`. A new .NET/SQL/tenant regression, different security finding, missing test or stale HEAD is not excepted.
4. **SRC-B6-04 — Review and provenance:** Check latest HEAD/base/mergeability, absence of unresolved source-level blocking findings/review threads; retain the exact CI run URLs, SHA/artifact hashes and this one-time Owner decision in the PR. Source analysis is not an independent physical DBA/Security sign-off.
5. **SRC-B6-05 — Platform policy:** Do not bypass branch protection, force-push, forge check statuses or disable required checks. If GitHub refuses merge or a non-excepted gate fails, stop without merging.

## 3. Explicitly excluded / remaining NO_GO

- **#53:** rollback-resistant external revocation high-watermark, B.3/B.4/B.5 same-transaction fence, safe restore and Worker/CASE retry authority are still **NOT_VERIFIED**.
- **#67:** actual APP-02A immutable CASE source/actor identity, B.3 session, B.4 tenant binding, B.5 permissions/epoch and B.6 policy decisions are **not** integrated. Current SQL parameter `@ExpectedCaseId` and maker-supplied hash are not live authorization.
- **#59:** full-tree npm security audit still **FAIL** with 11 HIGH; security issue remains open until a separately independently reviewed remediation passes. **#68:** intermittent A11-09 root cause remains open, even if not reproducible at final source.
- Real PREPROD private SQL/Vault principals, independently granted roles/DBA audit, legal L0 authority, two real physical tenants, G-B01..05 / 30 B.1 cases, B.7 PITR/restore, B.8 immutable external audit, B.9 oracle, original production P0 gates, EV-08 and live AC-11 remain **NOT_RUN / NOT_VERIFIED**.
- No production/Pilot/Go-Live, APP-02C/D/E authorization, real business posting, policy-gated tenant admission or runtime integration of these operator-only procedures. This code-only decision does not authorize invoking the migration or provisioning real SQL roles.

## 4. Evidence and permissible status

Initial independently reviewed/fixed source [`a7ce689dffefe577b465519ca62f078465696a6e`](https://github.com/chung6a8m/fmcg-distribution-dotnet/commit/a7ce689dffefe577b465519ca62f078465696a6e):

- [B.6 workflow #44270955608](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955608): SUCCESS 2/2 jobs; 4/4 Ubuntu unit and 2/2 Windows SQL LocalDB tests, zero failed/skipped; source artifact `14413340209` (SHA-256 `97b1d08e2389b27da8af69611c3cf9d59aabe315bfc9dafe992bb7e8352ab534`), SQL artifact `14412298255` (SHA-256 `9b031b8360f66782f53182567300f888e5bf7cef79d16af14cfb1f111b951451`).
- B.2 [#44270955584](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955584), B.3 [#44270955646](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955646), B.4 SQL [#44270955573](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955573), B.4 bounded web [#44270955596](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955596), B.5 [#44270955618](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955618): SUCCESS on that source.
- [APP-00A #44270955564](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44270955564): **FAILED** only at full-tree npm audit (11 HIGH, #59) after 296/296 APP-01E/APP-02A .NET/SQL tests passed, A.4 24/24 groups, 1440/1440 cells, 5/5 validator mutations, production-only npm audit passed. The downstream full APP-00A frontend tests were skipped.
- Independent [review #5953417961](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/66#pullrequestreview-5953417961): four B.6 source findings corrected. **After adding this decision or other source changes, all CI sign-off must be refreshed against the actual final source SHA before merge.** Do not inherit prior green checks onto a changed HEAD.

Only after these checks may status be **`MERGED / B6_GITHUB_HOSTED_CI_TESTED / KNOWN_HIGH_SECURITY_DEBT_59_OPEN / REVOCATION_53_OPEN / CASE_INTEGRATION_67_OPEN / PHYSICAL_PREPROD_NOT_VERIFIED`**.

It must **not** become `POLICY_GOVERNANCE_VERIFIED`, `PREPROD_FOUNDATION_VERIFIED`, `G-B01..05 PASS`, `APP-00A PASS`, or any production acceptance. 
