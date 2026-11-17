# APP-02B.4 — Operator-owned PREPROD tenant provisioning runbook

**Not executed / not authorized for a physical environment by PR #58.** This runbook describes an operator change, **not** a deployment claim. Requires explicit DBA/Security approval for actual PREPROD host, approved primary Control Plane/SQL/Vault targets and independent reviewer; never run against production, LocalDB outside disposable tests, or any unowned tenant database.

## Prerequisites, reviewers and isolated principals

1. Record named DBA change approval, Requester / Approver / Provisioner / Verifier / Security Suspension identities as distinct SQL Server logins, reviewed source commit SHA, migration file SHA-256, target privately routed SQL Server address, DB naming manifest, SQL access deny grants, Vault token ACL version, rollback/restore decision and immutable external audit-sink plan. **Do not seed or enable operator rows automatically**. Existing Identity control-plane migration `B3_20261117_001` remains subject to separate B.3/#52 operator approval.
2. Create or select the operator-owned **Control Plane** SQL database with independent DBA approval and secure TLS. Apply `src/FmcgDms.Preprod.Tenancy/Migrations/20261118_001_registry_control_plane.sql` using approved DBA identity on that target. It refuses system DB, existing partial schema and unknown version; replay with the exact version is idempotent. Require reviewed schema/permission fingerprints and separate backup of registry/audit; **never run via startup/Worker/HTTP**.
3. DBA creates explicit `PreprodTenantOperators` rows and least-privilege `EXECUTE` grants on required procedures, with distinct authenticated logins and no implicit tenant business data access. `CanRequest`, `CanApprove`, `CanProvision`, `CanVerify`, `CanSuspend` are separately granted. Revoke direct `INSERT/UPDATE/DELETE` on registry and event tables for operators; require read-only minimum metadata exposure. A DBA/system administrator cannot infer tenant business access. **No default operator**.
4. Five **separately ACL-scoped Vault KV-v2 role secrets**: `preprod/data/tenant-provisioning/{requester|approver|provisioner|verifier|security}` with each role's own limited `registryConnectionString`. **Only `provisioner`** may contain `masterConnectionString` (privileged DDL). The verifier role alone can read its own approved tenant's `preprod/data/tenant-provisioning/probe/{tenantId:N}` with the scoped probe `connectionString`. These secrets/tokens **must be split by actual role**; the current offline CLI retrieves the operator data through its token and must only be run on an authorized isolated host with an appropriate ACL. Provisioning requires separate SQL DDL principal; verification probe principal must deny cross-tenant and Control Plane reads and *all* marker writes. Do not mount provisioning token on API/Worker. Control Plane SQL must use verified TLS, `TrustServerCertificate=False`.
5. Independently verify Caddy/API still health-only; `Runtime:ControlPlaneEnabled=false`, `Runtime:TenantRoutingEnabled=false`, `Admission=NO_GO`. No public tenant/provisioning endpoints, no tenant routes, no business mode. Physical Vault/port/RUNTIME-01/02 witnesses remain pending #54.

## Approved operator sequence (example syntax, not a live instruction to execute)

After a separate operator change authorizes a **nonproduction, privately scoped** target, set `DOTNET_ENVIRONMENT=Preprod` and `APP02B4_VAULT_ADDR=https://<approved-private-vault-host>` in the controlled operator runtime; mount a restricted file `/run/secrets/vault_tenant_provisioning_token`. Never supply SQL passwords, Vault token, database names or connection strings on the command line. Use `dotnet run --project src/FmcgDms.Preprod.TenantTool -- <action> <tenant-guid> [...]`.

| Action | Mandatory separate actor / input | Expected state |
|---|---|---|
| `request <TenantId> <lowercase-tenant-key> <ChangeRef>` | Requester (records only server-derived DB name) | REQUESTED |
| `approve <TenantId> <ApprovalRef>` | Different Approver SQL login | APPROVED |
| `provision <TenantId>` | Scoped DDL operator, leased exclusive; SQL creates DB and marker, replays matching script | VERIFYING, still blocked |
| `verify <TenantId>` | Separate verifier with probe secret and peer tenant physically present | Recorded witness, still VERIFYING |
| `activate <TenantId>` | Approver with current rowversion + verification witness and authority | ACTIVE in registry **only**, not API/Worker routing |
| `suspend <TenantId>` / `revoke <TenantId>` | Authorized security actor; epoch fenced in same Registry transaction | SUSPENDED/nonroutable |
| `start-reactivation <TenantId> <ApprovalRef>` | New authorization and re-verification, still SUSPENDED fence | REACTIVATION_VERIFYING/nonroutable |
| `verify <TenantId>` then `activate <TenantId>` | Distinct verifier/approver; final CAS denies concurrent revoke | ACTIVE if current gates |
| `abort-reactivation <TenantId>` | Verifier closes pending attempt | SUSPENDED |
| `decommission <TenantId> <ArchiveApprovalRef>` | Approved change and freeze | DECOMMISSIONING |
| `complete-decommission <TenantId> <ArchiveEvidenceRef>` | Independent verifier, protected records and archive | DECOMMISSIONED, **DB retained** |

The first tenant cannot receive B4 `verify` without a second independently registered/provisioned peer tenant, by design. If any check fails: stop, keep tenant nonroutable, preserve existing SQL and lineage, open an explicit remediation/CASE ticket. A database created but not marker-bound is quarantined; never assume a retry can adopt it automatically. Never use `DROP` as an implicit cleanup. An actual SQL DDL change and user grants require more owner-level signoff than this PR contains.

## Evidence/denials, teardown and future handoff

Capture independent SQL SELECT of registry `TenantId,State,BindingId,BindingVersion,SecurityEpoch,RowVer`, tenant markers/schema ledgers, `SUSER_SNAME()` per role, peer/control read denied errors, own marker UPDATE denial, lease-collision and stale CAS negative, before/after full-table fingerprints, no orphan adoption, retained DB evidence, ticket IDs, UTC timestamps, Vault ACL audit/version and no externally exposed SQL ports. Compare expected vs actual and keep redacted immutable artifact manifest with reviewed HEAD, TestRunId, migration SHA, operator and separate independent reviewer. No secrets in logs, screenshots, CI artifacts or runbooks.

The dedicated `B4` workflow is **disposable GitHub-hosted CI only**. It cannot witness actual physical PREPROD networks, SQL principals, data retention, real Identity actor membership, cross-workload revocation, externally immutable audit or PITR. Open B.4 implementation findings as blocking where they affect safety; hand remaining authorization to B.5, PITR to B.7, audit to B.8, full physical two-tenant B.1 acceptance to B.9. `G-B01..05` all remain NOT_VERIFIED.

See [B.4 contract](../../docs/roadmap/app-02b4-tenant-registry-provisioning.md) and frozen [B.1 matrix](../../docs/security/app-02b1-acceptance-evidence.md).

**Mandatory independent Vault endpoint pin:** Before starting the offline operator CLI, mount a root/operator-controlled read-only file at `/run/secrets/b4_approved_vault_endpoint` containing exactly the approved canonical HTTPS Vault URI (including trailing slash), recorded in the change ticket. CLI denies mode or endpoint mismatch **before reading its Vault token file or making any HTTP request**. Never derive this anchor from `APP02B4_VAULT_ADDR`; both must agree, and do not expose the anchor file for API/Worker mutation.

**Role-token handling:** Separate requester/approver/provisioner/verifier/security Vault policies and distinct SQL principals; on each CLI invocation mount **only that role's token** in `/run/secrets/vault_tenant_provisioning_token`. Do not authorize a token to read multiple role secrets. The source code routes each CLI action to a fixed Vault role path (no request-supplied path). The *verifier* must not read the provisioner's DDL secret; a *provisioner* must not read peer probe secrets; only a specifically approved per-tenant verifier Vault ACL can read that exact tenant probe path. Policy templates: `deploy/preprod/vault-policy-b4-*.hcl`; actual token issuance/access remains unverified PREPROD operator work.
