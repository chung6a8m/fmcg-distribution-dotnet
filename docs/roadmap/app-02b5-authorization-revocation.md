# APP-02B.5 — Authorization & Revocation (PREPROD source-only)

**Status on this PR:** SOURCE_IMPLEMENTED / FINAL_HEAD_CI_AND_REVIEW_PENDING. B.5 is NOT AUTHORIZATION_VERIFIED. The original B.1 G-B01..05 / 30 physical PREPROD cases remain NOT_RUN / NOT_VERIFIED. No tenant business route, no Worker dispatch, no production/pilot/APP-02C/D/E authorization.

## 1. Contract and authorized boundaries

Owner-approved B.5 requires deny-by-default RBAC with server-bound actor/session, explicit tenant membership, current role/permission/resource scope, Maker/Checker independence, monotonic revocation and restore-safe fencing. An operator or System Administrator has no implicit tenant data authority. Caller-supplied tenant, resource or SQL connection hints are never authorization.

The standalone B.5 code package has three **operator-only** forward SQL Server migrations:

- **001 independent fence:** B5TenantFence (tenant/binding/registry/fence epochs and ACTIVE/SUSPENDED status), B5Membership (member epoch and revoke), B5Role, B5ActorRole, B5ActionCatalog (explicit READ/WRITE/APPROVAL classification), B5RolePermission (exact resource key), immutable B5ApprovalSource/B5ApprovalDecision, B5SecurityEvent; SQL roles b5_runtime (assert only) and b5_revoker (revoke/suspend only). No default users, tenant rights, ACTIVE rows, or generic wildcard allow.
- **002 Identity bridge:** applied only after B.3 in the approved Identity Control Plane database. b5_AssertCurrentSession locks B.3 session and user rows until caller COMMIT, checks live user, security stamp, lockout, MFA, current epoch and revoke. The caller has EXEC only; no direct Identity table read.
- **003 Registry bridge:** applied only after B.4 in the approved Registry Control Plane database. b5_AssertCurrentTenant locks the current B.4 registry row through COMMIT, checks ACTIVE, binding ID, **exact operator-pinned physical DatabaseName** and SecurityEpoch. Caller has EXEC only; no direct Registry table read.

All three migrations reject system databases, missing predecessor history, partial or unknown version. They are **not** applied on app startup. Operator grants and independent DBA evidence are distinct from disposable CI SQL principals.

SqlAuthorizationFence.ExecuteAsync requires **operator-pinned** tenant database name and SqlControlPlaneGuards tied to operator-pinned Identity and Registry database names; raw client database paths and bogus identifiers are denied. CI only: the A SQL runtime principal has INSERT to A, **not** B; a valid A authorization followed by B-target DML is denied by SQL and verified with before/after A/B SHA-256 table fingerprints. A registry mismatch is rejected before DML. These are disposable LocalDB witnesses and cannot replace real SQL/Vault principal review. Approval actions require a catalog-classified `APPROVAL` permission and an existing immutable SQL approval source ID; MakerId/InitiatorId are read from source, not accepted from request. B5ApprovalDecision insert and the effect commit/rollback together, with duplicate-check/replay denied. Within one SqlConnection and SERIALIZABLE SqlTransaction, it verifies current B.4 Registry and B.3 Identity (real SQL locks), then B.5 role/membership/resource/fence via b5_AssertAuthority (UPDLOCK/HOLDLOCK) and only then runs the protected tenant-local SQL effect. Each source mutation must use this SAME SQL connection and transaction. The B.3/B.4 revoke and suspend operations cannot overtake a transaction already holding their SQL locks. An effect after their COMMIT fails current-state checks.

**Not supported:** external payments, a separate SQL connection, remote transaction, asynchronous dispatch, offline writes or a production business handler. PREPROD API and Worker continue NO_GO; no authorization service is registered on a tenant business route.

## 2. GitHub-hosted source/SQL verification cases

| Case | Executed behavior in disposable CI |
|---|---|
| CI-B5-01 | Independent fence + exact B.3/B.4 base and B.5 bridge migrations; first apply and replay |
| CI-B5-02 | Two distinct tenant SQL DBs plus separate Identity, Registry and fence authority databases |
| CI-B5-03 | Exact membership, RBAC/resource and actor binding; wrong tenant, actor, binding, session/epoch; approval action source required and checker≠maker/initiator |
| CI-B5-04 | B.5 revoke/suspend SQL principal separated; B.5 revoke waits on protected COMMIT; no second effect on stale epoch |
| CI-B5-05 | Real B.3 session row revoke blocks on protected COMMIT; stale JWT/session denied |
| CI-B5-06 | Actual B.4 cp_TenantTransition SUSPEND blocks on protected COMMIT; stale Registry state denied |
| CI-B5-07 | SQL runtime cannot directly read membership or write B with valid A authority; independent A/B fingerprints unchanged on malicious B-target attempt |
| CI-B5-08 | Source SHA, migration hashes, TestRunId, three SQL witness reports (fence/control/approval) and redacted CI artifacts |

Implementation workflow: .github/workflows/app02b5-authorization.yml (Ubuntu unit + Windows SQL Server LocalDB). Existing APP-01E/APP-02A/B.2–B.4/frontend/PWA/Playwright regressions remain required. All B.5 CI engineering results are **GITHUB_HOSTED_CI_ONLY**, never actual PREPROD.

## 3. Blocking gaps for full B.5 / restore-safe acceptance

**REV-01 — Non-rollback external high-watermark coordination (BLOCKING):** While commit-time B.3 and B.4 checks are now implemented and CI-tested, their existing session revoke and tenant lifecycle transitions do **not yet atomically advance the separately recoverable B.5 authority**. Restoring an older B.3 or B.4 Control Plane snapshot could resurrect an earlier valid session/tenant epoch. Before business admission, implement an independently reviewed, non-bypassable same-commit link for every revocation/suspend and reactivation path, enforce monotonic epochs across restores, and test the old-snapshot negative. Issue #53 remains OPEN; do not call B01-03/B01-06 fully verified or SOURCE SECURITY CLEARED.

**ADMIN-01 — No approved role enrollment/activation:** B.5 source deliberately seeds no permissions, does not expose API role administration, and lacks independent real PREPROD Maker/Checker operator provisioning and current permission change approval. A CI-only DBA fixture inserts test role data directly. B.6 will govern business policy approvals, but B.5 must obtain its own approved tenant membership/RBAC operator lifecycle before activation. Do not infer real role grants or business policy defaults.

**REAL-01 — Missing operator environment:** #52/#54/#60 require real PREPROD SQL/Vault/DBA principals, separate identities, actual port/TLS/security witnesses and approved migrations. #55 immutable audit, #56 independent B.9 30-case acceptance and B.7 PITR drill remain future dependencies. No actual RUNTIME-01/02, G-B01..05 or physical restore assertions have been executed.

**SEC-59 — Full npm audit remains FAILED:** #59 is OPEN HIGH security debt. The B.4-specific DEBT-01 source merge exception applies only to PR #58 and cannot be inherited. Do not relabel APP-00A as PASS, waive checks or authorize release on this PR.

### Independent review findings: containment, not authority

**SAFE-DEFAULT (fixed in this draft):** `SqlAuthorizationFence.ExecuteAsync` now denies every ordinary production/runtime invocation, before opening a SQL connection. Only the internal test-only hook against disposable `(localdb)\\...` permits the existing CI harness. This is a containment guard, **not** B.5 authorization acceptance.

**SQL-BOUNDARY-01 / #64 — SOURCE LOCALDB FIX, PHYSICAL BLOCKING:** The B.4 registry stored procedure now denies mismatched `DatabaseName`; B.5 pins the expected database and the CI runtime login can write Tenant A but cannot write Tenant B. The test executes **valid A actor+permission + malicious B three-part SQL target**, and also direct B SQL, verifying both fail and A/B before/after hashes are unchanged. However the generic callback still takes a raw SqlConnection and physical PREPROD principal grants, independent full tenant DB marker and operator-governed command confinement have NOT been verified. Do not enable handlers until source API confinement and actual DBA/SQL identity review complete. `CI_SOURCE_NEGATIVE_PASS` is not `B01-01 PASS`.

**CHECKER-01 / #65 — SOURCE LOCALDB FIX, OPERATOR APPROVAL BLOCKING:** The old nullable, caller-supplied `MakerId` is removed. `B5ActionCatalog` classifies exact permission as READ/WRITE/APPROVAL; an `APPROVAL` requires a valid `ApprovalSourceId` and SQL loads immutable Maker/Initiator/Resource/SourceHash under lock. A single-use decision is inserted in the guarded transaction with the effect. SQL tests deny missing/forged/wrong-resource/self-maker/initiator-checker, validate rollback, immutable UPDATE/DELETE rejection, replay denial and unchanged peer hash. CI fixture DBA insertion is **not** an authorized real workflow approval, so independent operator lifecycle, permission governance, real audit and PREPROD B01-05/B02-06 remain BLOCKING.

## 4. Strict exit rule

Do not merge or mark B.5 AUTHORIZATION_VERIFIED while REV-01/ADMIN-01 and #64/#65 physical/operator acceptance remain unresolved or full required source security checks fail. Even a future B.5 source-only merge, if separately Owner-authorized, cannot promote G-B01..05 from NOT_RUN. No real PREPROD admission, production, pilot or APP-02C/D/E can be inferred from B.5 CI evidence.

See the [operator migration and denial runbook](../../deploy/preprod/authorization-operator.md), B.1 threat model and frozen acceptance matrix.
