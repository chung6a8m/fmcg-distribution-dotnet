# APP-02B.5 — Authorization & Revocation (source-first, PREPROD only)

**Status:** SOURCE_IMPLEMENTED_IN_PR / REVIEW_AND_CI_PENDING. Actual PREPROD authorization, B01-01..06 and all G-B01..05 (30 cases) remain NOT_VERIFIED / NOT_RUN. No production/pilot/real business admission; APP-02C/D/E remains NOT_AUTHORIZED.

## Frozen scope and negative guarantees

B.5 follows the 10 Owner decisions and B.1 threats/acceptance. RBAC requires active tenant membership plus an explicitly assigned, enabled tenant role and exact permission + resource scope. System Admin is **not** implicitly a tenant business actor. Only server-validated B.3 session/actor, B.4 binding/epoch, and resource-owner tenant may be used. Missing, stale, cross-tenant or self-approved Maker/Checker identity is denied; B.6 approval/policy values remain UNSET.

The new standalone, DBA-applied migration B5_20261118_001 creates a **separately recoverable** SQL Server revocation authority: B5TenantFence, B5Membership, B5Role, B5ActorRole, B5RolePermission and B5SecurityEvent. No seeded tenant/actor/role, no default authorization, no API/Worker automatic DDL, no implicit tenant ACTIVE grant. Runtime role has EXECUTE only on b5_AssertAuthority; independent revoker role has separate CAS revoke/suspend procedures. Real SQL/DBA/Vault grants require operator signoff.

SqlAuthorizationFence.ExecuteAsync enforces one SERIALIZABLE SQL transaction on one SQL connection: b5_AssertAuthority checks exact tenant/registry/fence/member epoch, actor/role/permission/resource and holds UPDLOCK/HOLDLOCK until the protected local SQL effect COMMIT. Independent revoke/suspend cannot overtake that COMMIT; a stale subsequent authorization is denied. It is **not** safe for HTTP side effects, a second database connection, or queued dispatch. PREPROD Worker continues NO_GO, API exposes no tenant business route.

## Scope-limited CI matrix

| Case | SQL/negative condition |
|---|---|
| CI-B5-01 | First schema apply/replay, no default accounts or rights, source-only operator migration |
| CI-B5-02 | Two physically separate tenant LocalDBs plus separate fence DB, explicit A-only grant |
| CI-B5-03 | Wrong resource tenant, actor, binding, absent session, Maker=Checker and runtime direct table read denied |
| CI-B5-04 | Protected SQL effect vs independent security revoke; revoke waits for in-flight COMMIT; stale epoch denied |
| CI-B5-05 | Suspended B and wrong B permission denied, zero peer tenant SQL writes |
| CI-B5-06 | Run ID, exact HEAD SHA, migration hash, redacted witness; CI-only, never real PREPROD |

Workflow: .github/workflows/app02b5-authorization.yml. Unit runs on Ubuntu and SQL security runs on Windows LocalDB. Existing B.2/B.3/B.4/APP-00A regressions stay required. The full npm audit remains blocked by #59, and the B.4-specific Owner security debt exception does NOT apply to this PR.

## Remaining security blockers for B.5 completion

1. **B.3 Identity SQL authority**: The required CurrentSessionCheck currently runs before the fenced SQL transaction. An independent B.3 logout/refresh-reuse session revoke may race between that preflight and the effect COMMIT. Before runtime admission, introduce an identity session+user assertion under the **same SQL transaction/instance/connection**, or an independently linearized revoke fence proven equivalent. This is a BLOCKING finding, not a passing B01-03 result.
2. **B.4 tenant registry authority**: B.4 suspend/revoke and ACTIVE/binding transitions need an atomic, durable B.5 authority update and current registry lock/check under the protected commit boundary. A restored old Control Plane snapshot must never re-enable a revoked tenant. #53 B.5/B.7 monotonic proof remains OPEN.
3. **Physical operator/security witnesses**: Real PREPROD Linux/Vault/SQL effective privileges, 2 actual tenant DBs, operator-authenticated identities and separate security/DBA review have not been executed (#52, #54, #60). B.7 must do real PITR/reconciliation; B.9 must execute the frozen 30-case G-B01..05 matrix.
4. **Security debt #59**: Current mandatory APP-00A full npm audit FAIL is a separate high severity release blocker. No silent waiver or inherited B.4 source-merge exception.

Do not merge or mark AUTHORIZATION_VERIFIED until the B.3/B.4 same-commit authority gap is addressed and source-level findings and relevant exact-final-HEAD CI are resolved. Even then, actual G-B01/B03 and PREPROD_FOUNDATION_VERIFIED require real physical evidence.
