# APP-02B.4 — Independent finalizer privilege retirement (Issue #60)

**Source class: GITHUB_HOSTED_CI_ONLY; physical PREPROD/DBA acceptance: NOT_VERIFIED.**
Owner: [OWN-APP02B4-SEC-01 and conditional MERGE-01](../decisions/owner-app02b4-code-only-2026-11-18.md).
PR [#58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58), blocker [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60).

## Trust boundaries

| Actor | SQL / Vault capability | Must never have |
|---|---|---|
| Provisioner | Owner-approved `CREATE ANY DATABASE`; scoped marker migration; grant `TAKE OWNERSHIP` only for approved tenant | Owner IMPERSONATE, retiring DBA Vault token, peer/control business privileges |
| Finalizer | Approved per-tenant `TAKE OWNERSHIP` + independently DBA-granted owner `IMPERSONATE`; `CanFinalize` | `CanRetireFinalizer`, DBA retirement Vault token, `sysadmin`, `ALTER ANY DATABASE`, `CONTROL SERVER` |
| Retirer | Independent DBA-controlled SQL login, `CanRetireFinalizer`, dedicated per-tenant Vault token; server rights limited operationally to revocation, login disable and session termination | `CanFinalize`, provisioner/Vault secrets, API/Worker token, unapproved tenant mutation |
| Verifier | Independent SQL marker read-only probe + `CanVerify` | DBA authority, tenant owner, finalizer/retirer tokens |

The DBA retirement credential is a privileged infrastructure actor that **must** be separately approved and audited. It is not a business actor and no business tenant route is opened by these procedures. A truly compromised SQL instance administrator cannot be sandboxed by application code; capture the separation and residual trust in the formal threat review.

## Ordered operation, crash handling

1. Approver freezes `ApprovedOwnerLogin`, `ApprovedProvisionerLogin`, `ApprovedFinalizerLogin`, `ApprovedRetirerLogin` and current binding/epoch against a named change ticket. Identity pairs are distinct and operator rows start disabled.
2. Provisioner creates and binds the approved tenant database. Finalizer claims a 5-minute `FINALIZING` lease, checks current SQL authority, performs the single approved ownership transfer and reads back `sys.databases.owner_sid`. **No finalization COMMIT** occurs here.
3. Retirer is a **different SQL principal/token/process**, verifies the same exact tenant and approved operation, disables the finalizer login and removes database TAKE OWNERSHIP, server owner IMPERSONATE and the tenant finalizer user. It terminates remaining SQL sessions for the login. If any step fails, leave `FINALIZING` non-routable.
4. `cp_CompleteFinalization`, callable only by the independently approved `CanRetireFinalizer` identity, reads back disabled login, absence of residual impersonation grant/DB user/active session, current owner SID and exact operation/binding/epoch. Only then write `FinalizerRetiredBy` and advance to `VERIFYING`.
5. A separate `CanVerify` user tests physical own/peer/control permissions and records a witness. Distinct approver may activate only with current `RowVer`, epoch/binding, owner SID and retirement receipt.

An interruption after SQL revocation but before the registry ACK can be retried by the same DBA retirement identity: the grants are never recreated and the `FinalizationOperationId` is retained. If an expired lease or suspended tenant blocks COMMIT, stop and open a controlled CASE. No auto-DROP, no unauthorized lease bypass, no reactivation without explicit approval.

## CI proof and physical handoff

Disposable Windows LocalDB checks two physical databases, SQL-principal impersonation, cross-tenant read and DDL denial, wrong owner, duplicate claims, revoked-inflight finalization, direct finalizer self-completion denial, retirement, disabled old login, removed grant/user and post-transfer retake denial, and lost-ACK recovery. GitHub artifacts identify source HEAD, migrations and TestRunId; they do **not** attest physical PREPROD.

**Separate human Security/DBA review required:** evaluate effective server and database permissions including role inheritance, session termination behavior and privileged DBA cleanup credential scope, Vault policy issuance and revocation, immutable audit evidence and external change approval. Physical SQL/identity/Vault probes, B.1 30 cases and G-B01..05 remain NOT_VERIFIED; no `TENANT_LIFECYCLE_VERIFIED` claim. Full APP-00A security audit Issue #59 must also PASS before conditional CODE_ONLY_MERGE can be exercised.

## Owner staffing disposition — solo development

[OWN-APP02B4-SOLO-01](../decisions/owner-app02b4-solo-governance-2026-11-18.md) authorizes one human Owner to perform both the DBA and Security reviewer responsibilities for B.4 source-level acceptance; it does **not** make those reviews two independent people. The role passes and independence deviation must be documented. **Runtime principals and credentials remain independent**: distinct per-tenant SQL logins, explicit DBA-retirer permissions, Vault secret ACLs, immutable lifecycle events, owner SID/epoch/lease/CAS checks and denied post-retirement reown tests remain mandatory. No source-only role authorization grants physical PREPROD verification or closes #60 by itself.
