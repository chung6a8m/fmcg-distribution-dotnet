# OWN-APP02B5-DEBT-01 — Bounded code-only integration with explicitly disclosed unresolved security and infrastructure debt

**Date:** 2026-11-18 (Asia/Bangkok). **Scope:** APP-02B.5 [PR #63](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/63) **only**. Owner direction in project discussion: *"Review vòng tiếp theo cho PR #63 và các yếu tố liên quan (PR, issue). Xử lý findings nếu có. Tôi chấp thuận nợ kỹ thuật các yếu tố hạ tầng và merge code-only."*

**Disposition:** `OWNER_APPROVED_B5_CODE_ONLY_MERGE / CONDITIONAL_SOURCE_ACCEPTANCE`. This is a new, narrow exception to the **timing of code integration**, not a statement that any outstanding security, physical PREPROD, recovery, operations, or business acceptance gate passed. B.4's earlier [OWN-APP02B4-DEBT-01](owner-app02b4-bounded-security-debt-merge-2026-11-18.md) applied ONLY to PR #58 and is not retroactively inherited.

## 1. Exactly what this authorizes

A merge to `main` of **inert B.5 source code, SQL migration files (never applied by runtime), tests, CI and documents**, provided the source-only compensating gates below are verified on the exact last PR HEAD. No B.5 API/Worker business entrypoint, no automatic schema application, tenant membership enrollment, role grants, SQL/Vault secrets, tenant ACTIVE rows or production data.

**Deferred obligations stay OPEN, not PASS**:

- [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53): missing independent rollback-resistant monotonic revocation high-watermark across existing B.3/B.4 operations and B.5 fence; old Control Plane snapshot, Worker/CASE recovery, restore and B.7 are not safe to authorize. This is **architectural security debt blocking runtime**, not merely a missing physical test.
- [#64](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/64): source-level tenant-DB binding and A→B SQL negatives repaired in LocalDB; real SQL principal/physical marker acceptance, actual operator binding and future safe public command API are pending. Raw arbitrary SQL `ExecuteAsync` remains internal to CI friend test assemblies and LocalDB only.
- [#65](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/65): optional request-supplied MakerId replaced by immutable SQL approval source and same-commit decision; approved operator provisioning, real independent Maker/Checker audit, source provenance and physical witnesses remain outstanding.
- [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59): **HIGH SECURITY DEBT**, not innocuous infrastructure debt. Full APP-00A `npm audit --audit-level=moderate` reports **11 HIGH** due to vulnerable dev-tooling `picomatch`, while production-only audit reports none. The Owner's explicit request for **code-only integration** is taken as acceptance of this **limited integration timing risk**, **not** a claim of audit success or approval to build/deploy untrusted artifacts. Keep the audit mandatory and FAILING, without lowering severity, skipping tests or suppressing advisory. Separate [draft PR #61](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/61) and #59 remediation remain open.
- #52/#54/#55/#56/#60, ADMIN-01, all 30 B.1 cases and G-B01..05, real PREPROD SQL/Vault/network/security witnesses, immutable operations/audit and B.7 PITR/restore all remain `NOT_RUN/NOT_VERIFIED`.

## 2. Non-negotiable code-only gates

1. **SRC-B5-01 — Source boundary:** Re-review complete latest PR diff against current `main`; zero unresolved *code-level defects* that enable runtime authorization, data leakage, privilege bypass or default grants. Preserve `business/`, `business.lock.json`, frontend dependencies and application startup/runtime behavior; no publish/deploy.
2. **SRC-B5-02 — B.5 final-HEAD CI:** Ubuntu unit and Windows LocalDB SQL tests PASS with zero required failures/skips. Verify five independent CI databases, cross-tenant/valid-A-wrong-B SQL denies, Maker/Checker/approval rollback, revocation vs COMMIT, B.3/B.4 current-state guards and corrupted recorded-schema replay. Retain source SHA, TestRunId and artifact SHA-256.
3. **SRC-B5-03 — Retained regressions:** B.2, B.3, B.4 and independent bounded Windows Node26 web/PWA/Playwright workflows PASS on the exact final HEAD. Main APP-00A .NET/SQL business/AC-11 oracle & pre-audit checks PASS and production-only npm audit PASS. **Full-tree npm audit remains FAIL** (#59), and downstream steps skipped: record them accurately, not as PASS. No edited/cancelled security gate.
4. **SRC-B5-04 — Review and provenance:** Compare HEAD/base/mergeability, role separation and public API exposure; check unresolved review threads and open issues. Record this specific Owner exception, SHA and current run/artifact links. Source code analysis is **not** an independent human Security/DBA review, and a solo Owner signoff must not be misrepresented as two humans.
5. **SRC-B5-05 — GitHub merge policy:** Do not bypass branch protection, force push or forge check statuses; if a **non-excepted** gate fails or GitHub rejects merge, remain DRAFT/NO_MERGE.

## 3. Scope of exception and status after integration

A successful source merge may be labeled at most:
`MERGED / B5_GITHUB_HOSTED_CI_TESTED / KNOWN_HIGH_SECURITY_DEBT_59_OPEN / REVOCATION_53_OPEN / PHYSICAL_PREPROD_NOT_VERIFIED`.

This does **not** mean `AUTHORIZATION_VERIFIED`, `G-B01..05 PASS`, `PREPROD_FOUNDATION_VERIFIED`, `RESTORE_VERIFIED`, `APP-02C/D/E` authorized, production P0, real EV-08/AC-11, pilot or go-live. Full authorization of actual runtime/physical PREPROD requires separate implementation, actual operator evidence, restoration fencing and independent acceptance.

**Decision boundary:** Integration of **unexecuted source** is approved; execution of unverified authorization or shipment/build of vulnerable dependencies is **not**. The Owner's approval of source merge is not a retroactive physical DBA/Security signature.
