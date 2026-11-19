# APP-02B.4 solo Owner / DBA / Security — technical source review disposition

**Review date:** 2026-11-18. **Reviewer provenance:** AI-assisted tool-based technical inspection in Owner's solo-developer workflow; not a second human reviewer, no forged GitHub approval, no live DBA or Vault session. **Owner policy:** [SOLO-01](../decisions/owner-app02b4-solo-governance-2026-11-18.md) and newer [DEBT-01](../decisions/owner-app02b4-bounded-security-debt-merge-2026-11-18.md). **Scope:** operator-only APP-02B.4 source, disposable CI. **Physical PREPROD and production:** NOT_VERIFIED.

## Separate review passes, with truthful evidence class

### Pass 1 — DBA responsibility: privilege, grants, ownership, crash/retry

| ID | Evidence / result on source | Deferred physical work |
|---|---|---|
| DBA60-01 | Source review: immutable SQL-approved owner/provisioner/finalizer/retirer; filtered owner/finalizer/retirer unique indexes **plus** serialized cross-tenant cross-role authorization denial, guarded by transaction application lock; SQL negative test added. `CODE_REVIEWED; SQL_CI_REQUIRED_FINAL_HEAD` | Actual two-tenant real PREPROD identities/grants `NOT_VERIFIED` |
| DBA60-02 | Non-sysadmin, non-dbcreator `CREATE ANY DATABASE` provisioner and restricted `CanFinalize`; finalizer denied SQL server global rights. Retirer is explicitly trusted DBA infra and CI uses a distinct sysadmin fixture. `SOURCE_ROLE_BOUNDARY_REVIEWED` | Inherited effective SQL permissions, role chains and DBA credential blast radius `NOT_VERIFIED` |
| DBA60-03 | `SqlTenantFinalizer` checks GUID DB, SQL source owner SID, target approved SID, current SQL claim/lease/epoch. No automatic DROP/adoption; state remains `FINALIZING`. `SOURCE_REVIEWED` | Physical SQL/backup/CASE evidence `NOT_VERIFIED` |
| DBA60-04 | `SqlTenantPrivilegeRetirer` uses a different Vault token and SQL identity; login DISABLE, REVOKE owner IMPERSONATE, tenant TAKE OWNERSHIP and DB principal, session termination for both login_name/original_login_name; `cp_CompleteFinalization` rechecks before SQL state change. `SOURCE_REVIEWED` | Actual role inheritance / privileged token rotation `NOT_VERIFIED` |
| DBA60-05 | Disposable LocalDB negative attempts deny original DDL owner read/update, finalizer post-transfer owner retake and peer owner transfer; owner SID independently read. `CI_SOURCE_TESTED` | External independent PREPROD negative witnesses `NOT_VERIFIED` |
| DBA60-06 | Tests cover five-minute lease, duplicate, stale token/failure, stable operation ID, revoke-inflight, lost ACK after revoked grants and retry. `CI_SOURCE_TESTED_PARTIAL_FAULT_MATRIX` | Full real crash/power/restore matrix `NOT_VERIFIED` |

### Pass 2 — Security responsibility: adversarial trust boundaries

| ID | Evidence / result on source | Deferred physical work |
|---|---|---|
| DBA60-07 | `cp_RecordVerification` NULL/rowversion/binding fence, SQL owner SID/retirement lineage checks; independent SQL-impersonated verifier denies peer/control metadata and own-marker UPDATE. `CI_SOURCE_TESTED` | Real session/JWT/Worker fences (B.5) `NOT_VERIFIED` |
| DBA60-08 | Vault endpoint read-only pin, HTTPS, fixed tenant KV-v2 role paths; distinct finalizer/retirer/Vault credentials; no public tenant API/Worker route. `SOURCE_REVIEWED` | Real Vault ACL issuance, revocation, packet captures and secret custody `NOT_VERIFIED` |
| DBA60-09 | Role-grant and session-clearing code inspected. Residual trust in DBA-retirer sysadmin equivalent and SQL Server effective permissions explicitly documented. `RISK_DISCLOSED` | Real PREPROD SQL Server versions, ACL/group inheritance, immutable off-host audit and operator review `NOT_VERIFIED` |
| DBA60-10 | Diff against main does **not** change `business/` or `business.lock.json`. No production, pilot, tenant business posting or external provider. `SOURCE_DIFF_REVIEWED` | Physical B.1 G-B01..05/30 and production P0 `NOT_VERIFIED` |

**Source security finding found and fixed during this review:** the original per-column SQL unique indexes prevented owner/owner and finalizer/finalizer reuse but not **owner-A = finalizer-B** or owner/retirer/provisioner cross-role collisions. The new transactional `cp_ApproveTenant` credential fence rejects cross-tenant cross-role overlap; negative tests and CI evidence are required before code integration. This finding is **not deferred debt**.

## Technical conclusions and explicit exceptions

1. **No outstanding confirmed source-level privilege-retirement defect in the inspected design after cross-role fix, subject to exact-final-HEAD SQL CI.** This is a tool-assisted technical assessment, *not* a separate human DBA/Security signature and not a PREPROD certification. The Owner-authorized solo workflow discloses `HUMAN_INDEPENDENCE_DEVIATION`.
2. [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60) source mitigation is code-only; leave the issue **OPEN as PREPROD DBA/Security effective-permission evidence debt**. Do not claim completed real credentials, actual physically denied owner retake or G-B01 PASS.
3. [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) is **known HIGH security debt**, not remediated. Owner's [DEBT-01](../decisions/owner-app02b4-bounded-security-debt-merge-2026-11-18.md) permits **source integration only** with truthful APP-00A FAIL and a separate bounded web-regression CI PASS. Do not call a dev dependency harmless, publish a vulnerable build, or suppress npm audit.
4. **Reviewer identity:** one Owner human, multiple organizational capacities authorized. This review document is **AI-assisted source inspection**, not an assertion that the Owner personally ran all DBA60 SQL queries or provided an independent human approval.
5. **Merge verdict:** `SOURCE_REVIEW_RECOMMENDS_BOUNDED_INTEGRATION_ONLY_IF_G_SRC_01..06_PASS`; final merge acceptance depends on the actual exact final HEAD, evidence, and GitHub's unmodified branch rules. A failure in any unexcepted gate is `NO_MERGE`.

## Evidence pointers, versioning

- [PR #58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58) — commit/diff/CI and Owner decision; source review is invalidated by changes to core source after this note without re-review.
- [APP-02B.4 CI](../../.github/workflows/app02b4-tenants.yml) — Ubuntu unit + disposable Windows LocalDB security witness.
- [Bounded web regressions](../../.github/workflows/app02b4-web-regressions-with-known-debt.yml) — Node26/npm ci/production npm audit, Vite/PWA/Playwright; the **mandatory full APP-00A audit remains failing and separate**.
- [DBA60 independent acceptance handoff](app-02b4-dba-security-independent-acceptance.md) — physical effective grants and human approval still `NOT_VERIFIED`.

This record does not close #59/#60, authorize any real PREPROD operation, change Business Baseline or serve as a second human reviewer signature.
