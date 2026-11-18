# APP-02B.4 — Tenant Registry & Provisioning (PREPROD only)

**Status 2026-11-18: PR #58 DRAFT / SOURCE_IMPLEMENTED / CI_PENDING / REAL_PREPROD_TENANCY_NOT_VERIFIED.** Strictly `OWN-APP02B-04` and the frozen [B.1 security contract](app-02b1-architecture-security-contract.md), [threats TM-05..10](../security/app-02b1-threat-model.md) and [B.1 30-case matrix](../security/app-02b1-acceptance-evidence.md). This increment does **not** amend or satisfy physical `G-B01..05`. B.2 and B.3 had separate scoped CI-only Owner exceptions: they do not grant B.4 an automatic CI-only merge waiver.

## Isolation, source ownership and API boundary

- **Control Plane Registry** is separate from each tenant physical SQL database; its SQL Server migration is `B4_20261118_001` and tenant marker migration is `B4_TENANT_20261118_001`. Reviewed, operator-applied SQL migration only. Neither API startup nor Worker can create, migrate or delete a database.
- Tenant DB physical name derives **only from server-side canonical TenantId GUID** as `FmcgPpT_ + GUID N`. TenantId/DB/connection string requested in HTTP headers/body/token cannot redirect a SQL connection. BindingId and BindingVersion and SecurityEpoch are authoritative; peer marker or missing/unknown version must deny.
- Control Plane operator is a separately approved authenticated SQL Server identity via `SUSER_SNAME()`. Registry read/write procedures require enabled operator permissions (no direct SELECT grant for registry tables); request and approval are different logins, provisioner and verification roles separated. An SQL admin may provision *infrastructure* but receives no tenant business-data grant by implication. No seeded administrators, memberships, tenant business accounts or production authorizations.
- Offline `FmcgDms.Preprod.TenantTool` is **not linked into PREPROD API or Worker routing** and is not deployed in API/Worker Docker images. Operator secrets are read from dedicated Vault KV-v2 paths/token scope, never CLI arguments. PREPROD API remains `FOUNDATION_ONLY / NO_GO` and `/auth/me` must continue saying `tenantAccess=DENIED`. Public enrollment/tenant switch/business endpoints stay absent.
- B.5 will introduce actual JWT/session-to-membership/permission/epoch routing, B.7 independent restore fencing and B.8 external tamper-evident audit; none are authorized by B.4 operator tooling.

## Lifecycle and transitions

| From → to | Required control and denial |
|---|---|
| none → REQUESTED | Operator `CanRequest`, server-generated DB name and BindingId; same TenantId + tenant key + requester + change ref is idempotent, mismatch denies |
| REQUESTED → APPROVED | Different enabled SQL login `CanApprove`, immutable approval ref; duplicate matching approval permitted, self-approval and state reentry denied |
| APPROVED or FAILED_RETRYABLE → PROVISIONING | Approved tenant, `CanProvision` and SQL 5-minute exclusive lease; operation ID is stable across retries; no initial retry from SUSPENDED/reactivation states |
| PROVISIONING → VERIFYING | Dedicated operator DDL principal, SQL session create lock, new DB/approved marker + idempotent tenant schema; only current matching live lease can complete; orphan/unmarked/mismatched DB **never silently adopted/deleted** |
| VERIFYING → FINALIZING → VERIFYING | Independent per-tenant `CanFinalize` SQL login, Vault ACL, five-minute leased ownership transfer and owner SID readback. `FinalizationOperationId` stable across retries; stale/peer/revoked completion denied. Still non-routable | 
| VERIFYING → ACTIVE | Different verifier `CanVerify`, exact registry RowVer & BindingId and SHA-256 witness, then separate admin activation CAS on currently verified RowVer, epoch, binding and witness time |
| ACTIVE → SUSPENDED | Security operator writes State + incremented SecurityEpoch in **one SQL transaction** before reporting success. Any non-ACTIVE state non-routable |
| SUSPENDED → REACTIVATION_VERIFYING → ACTIVE | Fresh admin change ref, retains NON_ROUTABLE until independent re-verification and CAS; stale RowVer, concurrent suspend or changed epoch deny; a failed attempt remains SUSPENDED/REACTIVATION_VERIFYING and is **never** converted to initial FAILED_RETRYABLE |
| ACTIVE/SUSPENDED → DECOMMISSIONING → DECOMMISSIONED | Admin change ref, independent archive completion ref; SQL metadata and tenant DB retained; **no DROP**, automatic purge or legal retention assumption |

**Narrow proof scope:** `TenantProbeVerifier` checks exact tenant marker and actively denies cross-DB/control-plane SELECT and own marker UPDATE using a separately scoped SQL credential. It does **not** prove real PREPROD independent membership, Vault/SQL grants, external immutable audit, health/recovery or B.5 permission fence. The registry's SHA-256 witness is a recorded proof hash, not a trusted independent reviewer signature or a replacement for physical B.1 acceptance evidence. A true security approver must review actual operator proof and physical rights.

## Test matrix and evidence

| Test ID | Observable check | Required evidence class |
|---|---|---|
| CI-B4-01 | Registry migration first apply/replay/unknown-version/partial/system DB denial | Disposable Windows SQL Server LocalDB |
| CI-B4-02 | Two physical DB IDs, marker/binding, wrong peer marker denied; real SQL Server-scoped login reads A but cannot SELECT B/control or UPDATE A marker | Independent SQL permission-denial readback |
| CI-B4-03 | Requested state and request idempotency; requestor cannot self-approve | SQL principal impersonation / ledger |
| CI-B4-04 | Claim lease collisions, stable OperationId after failed retry, nonroutability | SQL row + event history |
| CI-B4-05 | No ACTIVE without verifier witness and final CAS | SQL readback |
| CI-B4-06 | Suspend increments SecurityEpoch; stale reactivation denied when revoke wins | SQL epoch/RowVer and negative |
| CI-B4-07 | Existing unmarked DB quarantined and preserved, no orphan DELETE | SQL before/after |
| CI-B4-08 | Decommission preserves both physical DBs and event ledger | SQL engine readback |
| CI-B4-09 | Operator PREPROD/HTTPS/TLS/no-LocalDB guard and non-ACTIVE routing denial | Ubuntu xUnit |
| REAL-B4-01 | Two operator-owned physical PREPROD tenant databases and separate scoped SQL principals | **NOT_VERIFIED** |
| REAL-B4-02 | Independent human security/DBA approval, Vault access and actual ingress/API/Worker deny | **NOT_VERIFIED** |
| REAL-B4-03 | Owner-side activation/disable/recovery and independent audit proof, physical fault matrix | **NOT_VERIFIED** |

GitHub workflows: `.github/workflows/app02b4-tenants.yml` (Ubuntu unit and Windows LocalDB SQL). Keep APP-00A (business/APP-01E.5/APP-02A.4/browser/PWA), APP-02B.2 runtime and APP-02B.3 Identity regressions; exact source HEAD, run attempt, source and migration hashes and artifact IDs must be recorded and reviewed. A PR merge is **blocked** until no blocking findings, exact final HEAD CI PASS and specific Owner merge authorization disposition. Other physical B.1 30 cases remain `NOT_RUN`, not `N/A`.

## Explicit unchanged restrictions

`business/` and `business.lock.json` unchanged. No `TENANT_LIFECYCLE_VERIFIED` claimed from CI-only. No business posting/tenant routing/production/Pilot, no A/B/Mixed admission or APP-02C/D/E. `B_NO_CHANNEL BLOCKED`, real AC-11/EV-08 and five production P0 gates `NOT_VERIFIED`. The actual PREPROD operator and subsequent B.5/B.7/B.8/B.9 gates remain independent obligations.

See [operator migration/runbook](../../deploy/preprod/tenant-provisioning-operator.md) and [APP-02B master roadmap](app-02b-preprod-foundation.md).

## Independent owner transfer and SQL authority addendum — source-only review

B4-SEC-01 is addressed in source with a two-principal transition, **pending Owner DBA/Security architecture acceptance and physical PREPROD witness**. A non-sysadmin `CREATE ANY DATABASE` provisioner creates a GUID-derived tenant DB and applies the marker, then stops at `VERIFYING`. An independently authorized DBA transfers database ownership to a separately approved non-sysadmin/non-dbcreator tenant owner. At `approve`, SQL freezes the expected `ApprovedOwnerLogin` and `ApprovedProvisionerLogin` identifiers. Both direct SQL verification and activation revalidate `sys.databases.owner_sid` against the approved owner and deny the original DDL owner. A CLI-supplied hash alone is not an authority to activate.

B4-TEST-02 is addressed at the **CI source verification level** with end-to-end `TenantProbeVerifier.VerifyAsync` under SQL Server `EXECUTE AS LOGIN` for separate verifier/scoped tenant login, negative peer/control/own-marker checks, and physical two-DB LocalDB SID comparisons. This CI impersonation does not establish real PREPROD Vault, physical operator credentials, API/Worker fencing, DBA approval, audit tamper resistance or G-B01..05 PASS. Strict `NOT_VERIFIED` remains until human independent evidence from the approved operator environment.

The only acceptable physical proof is an independently recorded row/owner SID and grant review, original provisioner SQL login SELECT/UPDATE denials on tenant tables, approved token/DB ownership chain, immutable evidence, and an explicit Owner disposition for *B.4*. CI-only merge exceptions for B.2/B.3 do not transfer. APP-00A audit Issue #59 blocks merge unless its required full tree gate truly passes; do not bypass or downgrade that gate.

## Owner decisions 2026-11-18 — B.4 SEC-01 and MERGE-01

[Decision record](../decisions/owner-app02b4-code-only-2026-11-18.md): Owner selected **Option 2** for both decisions: an **independent DBA-controlled Finalizer Automation** and a separate, strictly conditional **CODE_ONLY_MERGE** for PR #58. The former replaces the *interim* manual ownership transfer design; dedicated finalizer implementation and fresh negative tests still required. The latter becomes eligible **only when** finalizer and privilege boundary are independently reviewed, **all** B.4/B.2/B.3/APP-00A required CI jobs and full npm audit PASS on exact final HEAD (including resolving [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59)), and no blocking findings remain. No inherited B.2/B.3 waiver, no skipped required tests, no merge from a stale HEAD.

**As recorded: OWNER_APPROVED / MERGE_GATE_NOT_MET / PR_DRAFT.** Maximum eventual source integration label: `MERGED / B4_GITHUB_HOSTED_CI_TESTED`. Physical PREPROD, REAL-B4-01..03, all B.1 30 cases/G-B01..05, `TENANT_LIFECYCLE_VERIFIED`, real AC-11/EV-08 and production P0 gates remain `NOT_VERIFIED`.

## B.4 Finalizer source implementation (2026-11-18)

Per Owner `OWN-APP02B4-SEC-01`, a dedicated `SqlTenantFinalizer` class, the operator-only `finalize <TenantId>` CLI action, SQL procedures `cp_ClaimFinalization`, `cp_CompleteFinalization`, `cp_FailFinalization`, and per-tenant Vault token scope implement independent DBA-controlled ownership transfer, fenced by current SQL State/BindingId/SecurityEpoch/OperationId/LeaseToken. SQL `cp_RecordVerification` and `ACTIVATE` independently reject missing finalization metadata or a changed owner SID. Unapproved owner, stale lease, concurrent suspend/revoke, and wrong tenant are denied. The revised **unapplied source-only** migration `B4_20261118_001` is not approved for physical PREPROD execution; do not apply it to an environment with a previously applied different checksum without a separately reviewed forward migration.

**#60 technical mitigation in source:** The new `SqlTenantPrivilegeRetirer` and per-tenant `retire-finalizer` command separate the DBA-controlled credential retirement stage from `SqlTenantFinalizer`. Finalizer leaves `FINALIZING` non-routable; an independent DBA actor disables the finalizer SQL login, removes server owner-IMPERSONATE, revokes per-database TAKE OWNERSHIP, drops the finalizer database user, terminates remaining login sessions, then commits through `cp_CompleteFinalization` (now retire-authority only). SQL-authoritative row/operation/binding/epoch/owner/privilege checks and required retirement lineage reject direct finalizer self-completion. Tests exercise post-transfer retake denial and lost-ACK retry. This does **not** replace separate physical DBA/Security review of exact effective grants and credential issuance. Until that independent signoff, #60 remains security-review PENDING; do not mark G-B01 or code-only merge PASS.

The narrower `CODE_ONLY_MERGE` decision `OWN-APP02B4-MERGE-01` does not waive privilege containment, Issue [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59), full APP-00A npm audit, or required final HEAD security review and CI. No PREPROD provisioning has been executed. Maximum eventual source label is `B4_GITHUB_HOSTED_CI_TESTED`; `TENANT_LIFECYCLE_VERIFIED` and physical `G-B01..05` stay `NOT_VERIFIED`.

### Two-principal retirement acceptance witness — Issue #60

`CanFinalize` and `CanRetireFinalizer` are separate authenticated SQL operator capabilities. The finalizer does **not** call `cp_CompleteFinalization` and never gets the retiring DBA token. `FINALIZING` is not routable. The DBA retirement actor uses one exact tenant Vault secret to disable the previous SQL finalizer login, revoke owner IMPERSONATE, revoke/drop its tenant database user and TAKE OWNERSHIP, terminate sessions and independently assert the current Control Plane authority before recording `FinalizerRetiredBy` / `FinalizerRetiredUtc`. SQL verification and activation require the independent retirement receipt. An injected lost ACK after revocation is recovered under the same durable `FinalizationOperationId` with no regrant and no blind retry to a different database. An unknown owner, stale epoch, incomplete cleanup, or wrong principal DENIES.

**Safety scope:** SQL LocalDB negative tests are `GITHUB_HOSTED_CI_ONLY`, and a separate human Security/DBA review of the privileged retiring actor, post-transfer grants, Vault issuance, alternate role paths and actual PREPROD topology is still mandatory. Closing issue #60 or claiming PREPROD B.1 gates requires those distinct evidences; do not infer approval solely from the source implementation or AI review. Issue #59 full npm audit remains non-waivable; PR #58 cannot merge while that gate fails.

## Solo Owner/DBA/Security decision (2026-11-18)

[OWN-APP02B4-SOLO-01](../decisions/owner-app02b4-solo-governance-2026-11-18.md) confirms this project uses **solo-developer governance**. One Owner may act in Owner, DBA and Security capacities; therefore no external GitHub reviewer accounts are required for the **source-only** C-B4-02 staffing rule. Distinct technical DBA/Security review passes on exact final HEAD and recorded `SOLO_OWNER_SELF_ATTESTED` / `HUMAN_INDEPENDENCE_DEVIATION_DISCLOSED` remain mandatory. This is not a code acceptance verdict or waiver of the #59 npm audit and #60 privilege review. Independently authenticated SQL/Vault principals, physical B.1 G-B01..05/30 and PREPROD evidence obligations remain unchanged and `NOT_VERIFIED`.

## Owner-directed bounded source integration with known security debt (2026-11-18)

[DEBT-01](../decisions/owner-app02b4-bounded-security-debt-merge-2026-11-18.md) supersedes only the C-B4-04/05 strict *source-merge timing* for PR #58: #59 full npm audit is a disclosed **OPEN HIGH security debt**, and APP-00A continues to fail, rather than being relabeled PASS. Mandatory compensating gates: all B.4/B.2/B.3 SQL/Identity CI, retained APP-00A pre-audit two-tenant .NET/ledger/business SQL oracles, separate Windows Node 26 `web/PWA/Playwright` regression job, zero source-level security findings including **cross-tenant cross-role SQL credential reuse**. Source review provenance is [solo DBA/Security technical assessment](../security/app-02b4-solo-source-review-disposition-2026-11-18.md); no second human reviewer or physical PREPROD witness claimed. #60 actual SQL/Vault/DBA permissions stay OPEN/NOT_VERIFIED. Real PREPROD and the B.1 thirty cases remain blocked; no production/pilot or business route.
