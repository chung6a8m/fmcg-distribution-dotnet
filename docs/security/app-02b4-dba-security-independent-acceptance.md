# APP-02B.4 — Independent DBA / Security acceptance handoff

**Review request created 2026-11-18.** Source PR [#58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58), technical security finding [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), Owner decisions [OWN-APP02B4-SEC-01 and OWN-APP02B4-MERGE-01](../decisions/owner-app02b4-code-only-2026-11-18.md).

**Current state:** `REQUESTED / INDEPENDENT_HUMAN_DBA_SECURITY_NOT_SIGNED / PHYSICAL_PREPROD_NOT_VERIFIED`. A successful GitHub-hosted LocalDB test is not an independent DBA/Security signoff. This document requests a *source-only* privilege-boundary review for conditional `CODE_ONLY_MERGE`. A later, separately authorized and physically witnessed PREPROD run is required for REAL-B4-01..03 and all B.1 G-B01..05/30 cases; it is not waived or relabeled by source merge.

## A. Exact review scope (not generic approval)

The reviewer must review the entire source/diff of PR #58 **at its final HEAD**, not merely this handoff or a prior report. Current reference for reviewer orientation: source `357206817aa2c65feed86fa75c72692858458fc5` (replace with actual final PR HEAD before decision). The relevant source files are:

- `src/FmcgDms.Preprod.Tenancy/SqlTenantProvisioner.cs`
- `src/FmcgDms.Preprod.Tenancy/SqlTenantFinalizer.cs`
- `src/FmcgDms.Preprod.Tenancy/SqlTenantPrivilegeRetirer.cs`
- `src/FmcgDms.Preprod.Tenancy/TenantRegistrySql.cs`
- `src/FmcgDms.Preprod.Tenancy/TenantProbeVerifier.cs`
- `src/FmcgDms.Preprod.Tenancy/Migrations/20261118_001_registry_control_plane.sql`
- `src/FmcgDms.Preprod.Tenancy/Migrations/20261118_001_tenant_marker.sql`
- `src/FmcgDms.Preprod.Tenancy/VaultTenantOperatorSecrets.cs`, `TenantOperatorAdmission.cs`, `src/FmcgDms.Preprod.TenantTool/Program.cs`
- `tests/FmcgDms.Preprod.Tenancy.SqlServer.Tests/`, `tests/FmcgDms.Preprod.Tenancy.Tests/`, Vault policies `deploy/preprod/vault-policy-b4-*.hcl`, operator [runbook](../../deploy/preprod/tenant-provisioning-operator.md), [B.4 retirement contract](app-02b4-finalizer-retirement.md).

Review TM-05 (peer isolation), TM-06 (epoch/revoke recovery), TM-07 (System Admin/creator boundary), and relevant B01-01/B01-04/B01-06 and B02-01/02 control edges from [frozen B.1 threats](app-02b1-threat-model.md) and [30 cases](app-02b1-acceptance-evidence.md). Source review alone does not close any physical gate.

## B. Trust-boundary review matrix (required, fail closed)

| ID | Independent task and negative assertion | Expected and evidence class | Current status |
|---|---|---|---|
| DBA60-01 | Compare every approved TenantId, canonical physical DB, current BindingId/BindingVersion/SecurityEpoch and SQL-frozen owner/provisioner/finalizer/retirer login; prove all actors distinct and finalizer/retirer/owner logins cannot be bound to peer tenant | SQL readback, review commit, unique-index/role evidence | NOT_REVIEWED |
| DBA60-02 | Inspect effective permission chains, including direct grants, roles, group inheritance, database ownership, `sysadmin`, `dbcreator`, `ALTER ANY DATABASE`, `CONTROL SERVER`, `IMPERSONATE ANY LOGIN`, `TAKE OWNERSHIP` and the DBA-retirer's capabilities. Explicitly document why an instance DBA is trusted infrastructure and not a tenant business principal | Full effective-rights diff; no unexplained inherited or unbounded finalizer grants | NOT_REVIEWED |
| DBA60-03 | Read `SqlTenantFinalizer` and `cp_ClaimFinalization`: 5-minute current lease, stable operation identity, approved owner SID and exact GUID-derived DB, failed unknown/foreign owner and no unguarded transfer to peer/control | Current-head code/SQL review and negative LocalDB witness | NOT_REVIEWED |
| DBA60-04 | Read `SqlTenantPrivilegeRetirer`, `cp_CompleteFinalization`, Vault and CLI: independent authenticated DBA actor, **disabled finalizer**, revoked owner IMPERSONATE, removed tenant TAKE OWNERSHIP and DB user, killed old sessions, current SID/epoch/binding/lease; SQL must deny direct finalizer self-completion | Review token-path/SQL principal map, rollback/retry policy, deny witness | NOT_REVIEWED |
| DBA60-05 | Specifically test that finalizer cannot **reacquire** ownership **after** retirement, nor impersonate approved owner or enter a new session; compare owner SID/registry and both tenant DB fingerprints before/after | Denied SQL error and unchanged A/B/control hashes; stale credential cannot bypass | NOT_REVIEWED |
| DBA60-06 | Inject crash **before SQL transfer**, **after transfer but before retirement**, **after privilege revocation but before Control Plane ACK**; check retry with same durable operation and new token, stale lease, concurrent `SUSPEND/REVOKE` and no partial ACTIVE or bypass | Timeline, fault point, lease/epoch/rowversion and SQL owner/grant readback; fail closed | NOT_REVIEWED |
| DBA60-07 | Inspect authoritative `cp_RecordVerification` and `ACTIVATE` for owner SID + independent retirement receipt (not only a client-supplied witness hash); verifier must be separate SQL-scoped credential, denied own marker UPDATE and peer/control reads | Two physical disposable CI tenant DB tests and source review; PREPROD separate | NOT_REVIEWED |
| DBA60-08 | Vault trust-anchor pin, exact one-tenant secret paths, separate operator tokens, issuance/rotation/disable after use, no API/Worker ingress or secret access, no logging of credentials | Source policy plus actual operator-provided ACL review; absence of actual issuance = NOT_VERIFIED | NOT_REVIEWED |
| DBA60-09 | Validate minimal grant lifecycle in deployment: SQL Server and certificate versions, login disable/active-session termination (including `EXECUTE AS` and inherited rights), proper independent human audit, rollback/restore and immutable external evidence | DBA privilege review + Security witness; physical operations remain NOT_VERIFIED until executed | NOT_REVIEWED |
| DBA60-10 | Check that `business/` and `business.lock.json` do not change; no tenant API/Worker route, no production/pilot, owner CI-only merge condition is not a physical PREPROD waiver | Diff and runtime boundary review; PASS only on exact final head | NOT_REVIEWED |

The SQL Server `sysadmin` role can bypass these application-level restrictions. A privileged DBA-retirer is a trust anchor; it must not be mistaken for a cryptographically isolated tenant principal. An independent human review must explicitly accept or reject that residual authority and its operational constraints.

## C. Read-only evidence collection reference (not an authorization to connect)

**Run only after DBA approves the exact environment and values, under independently authenticated, read-only review authority.** Replace the placeholders with values **from signed approval and authoritative Control Plane**, never an HTTP/CLI request. Do not paste secrets or raw connection strings into artifacts.

```sql
-- The DBA must bind all parameters from independently approved source.
DECLARE @tenant uniqueidentifier = '00000000-0000-0000-0000-000000000000'; -- replace!
DECLARE @finalizer sysname = N'APPROVED_FINALIZER_SQL_LOGIN';               -- replace!
DECLARE @owner sysname = N'APPROVED_TENANT_OWNER_SQL_LOGIN';               -- replace!
DECLARE @retirer sysname = N'APPROVED_RETIRER_SQL_LOGIN';                   -- replace!

-- Run on the independently approved CONTROL PLANE database.
SELECT TenantId,DatabaseName,BindingId,BindingVersion,SecurityEpoch,State,
       ApprovedOwnerLogin,ApprovedProvisionerLogin,
       ApprovedFinalizerLogin,ApprovedRetirerLogin,
       FinalizationOperationId,FinalizationLeaseUntilUtc,
       FinalizedBy,FinalizedEpoch,FinalizedBindingId,
       FinalizerRetiredBy,FinalizerRetiredUtc
FROM dbo.PreprodTenantRegistry WHERE TenantId=@tenant;
-- Run on master, with independently authorized metadata visibility:
SELECT name,owner_sid,USER_NAME() AS ReviewingDatabaseUser
FROM sys.databases
WHERE name=N'FmcgPpT_' + LOWER(REPLACE(CONVERT(nvarchar(36),@tenant),N'-',N''));
SELECT name,is_disabled,type_desc FROM sys.server_principals
WHERE name IN (@finalizer,@owner,@retirer);
SELECT grantee.name AS Grantee,permission_name,state_desc,major_id
FROM sys.server_permissions p
JOIN sys.server_principals grantee ON grantee.principal_id=p.grantee_principal_id
WHERE grantee.name IN (@finalizer,@owner,@retirer);
SELECT s.session_id,s.login_name,s.original_login_name,s.status
FROM sys.dm_exec_sessions s WHERE s.login_name IN (@finalizer,@owner,@retirer)
   OR s.original_login_name IN (@finalizer,@owner,@retirer);
```

**Caution:** These queries do not by themselves prove all inherited grants or successful **denial**. The authorized independent DBA must separately collect effective-role membership/permission maps and execute **negative** operations with real role-scoped credentials: old finalizer `ALTER AUTHORIZATION` against own/peer/control, owner `IMPERSONATE`, peer database SELECT, own marker UPDATE. Capture precise SQL ERROR and A/B/control pre/post hash. Never run negative ownership-change commands under a sysadmin fixture or on any unapproved target; stop and escalate on a successful forbidden operation. If using SQL `EXECUTE AS` for a disposable CI test, label that **CI**, not actual PREPROD authentication.

## D. Evidence manifest and signoff (to be completed by actual reviewers)

| Field | Value |
|---|---|
| PR HEAD / reviewed git commit | PENDING_INDEPENDENT_REVIEW |
| Migration/workflow hashes and CI run attempts | PENDING_FINAL_HEAD |
| Build/TestRunId and artifact SHA256 | PENDING_FINAL_HEAD |
| DBA reviewer (human, independent from author/provisioner) | NOT_ASSIGNED |
| Security reviewer (human, independent from DBA/author) | NOT_ASSIGNED |
| Approved change ticket, reviewed SQL environment/tenant A+B | NOT_PROVIDED |
| Scope and effective grants/owner SID/roles pre/post | NOT_VERIFIED |
| Denied post-retirement retake, owner impersonation, peer/control | NOT_VERIFIED_IN_REAL_PREPROD |
| Crash/restart, stale lease, revoke-race, negative trace | CI_SOURCE_ONLY; PREPROD NOT_VERIFIED |
| Independent code-level decision | NOT_SIGNED |
| Physical PREPROD B.1 30-case evidence / G-B01..05 | NOT_RUN / NOT_VERIFIED |

The **source-only** signoff is distinct from a physical PREPROD signoff. It may conclude `B4_CODE_SECURITY_ACCEPTED` (only after human reviewers examine actual final HEAD and required CI), with physical gates explicitly `NOT_VERIFIED`. A missing signature, changed HEAD, unresolved security finding or failed APP-00A audit remains **NO_MERGE**, regardless of successful B.4 LocalDB tests.

## E. Decision requested

The independent DBA and Security reviewers must **each** comment on [PR #58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58) or [issue #60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), identify the exact reviewed commit and test evidence, and give one of `ACCEPT_CODE_BOUNDARY`, `REQUEST_CHANGES`, or `BLOCKED_MISSING_EVIDENCE`. Their review cannot be inferred from an automated code comment. If reviewers are not available or do not approve, keep both issue and PR open; **do not self-sign on their behalf**.
