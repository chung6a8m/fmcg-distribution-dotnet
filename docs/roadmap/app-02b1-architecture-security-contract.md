# APP-02B.1 — Architecture & Security Contract (PREPROD only)

**Contract state: CONTRACT_APPROVED (DOCS_ONLY, 2026-11-17).** Owner authorization is **10/10 OWNER_APPROVED** under [OWN-APP02B-01..10](../decisions/owner-app02b-2026-11-17.md). This is a **docs-only design increment**, not a deployed or verified security control. The B.1 design contract passed independent documentation/security review with the reactivation-fencing finding addressed and exact final-HEAD CI required before merge ([closeout](../closeout/2026-11-17-app-02b1.md)). Approval of the contract is **not** verification of any implemented security control. The complete APP-02B program remains **NOT_IMPLEMENTED / G-B01..05 NOT_RUN** until separately evidenced. Related: [APP-02B roadmap](app-02b-preprod-foundation.md), [threat model](../security/app-02b1-threat-model.md) and [30-case acceptance/evidence contract](../security/app-02b1-acceptance-evidence.md).

## 1. Authority, inheritance and explicit non-goals

1. **PREPROD/NONPRODUCTION exclusively:** No production deployment or mode admission; no live cash, seller inventory, invoice, accounting/provider interaction, Pilot, cutover, tenant go-live, APP-02C/D/E or offline business writes. **B_NO_CHANNEL is BLOCKED**; price, credit, terms, legal retention and real-world approval thresholds remain **UNSET**. Business Baseline in business/ and business.lock.json is immutable.
2. **Reference is not authority:** Existing Program.cs wires SyntheticAuth and in-process App02a3HostedWorker; TenantConnections.OpenSyntheticScratchAsync only allows two nonce-owned LocalDB databases in Development/IntegrationTest. APP-02A.4 proves 12 A11 groups on synthetic LocalDB only. None of that constitutes PREPROD Identity, Worker, tenant provisioning or production-grade permissions. No route under /api/synthetic/* or /api/diagnostics/* may be exposed or granted as PREPROD business authority.
3. **Non-negotiable business invariants:** Tenant data/membership isolation (BR-01/POL-01; AC-01), durable same-identity event semantics (BR-16/POL-16; AC-11), restore revocation and external-effect fence (BR-17/POL-17; AC-13) and original whole-Kho-01 writer/cutover constraints (AC-04/16) remain binding, not proven by this contract. Five original production P0 gates and real EV-01..15 including EV-08 remain **NOT_VERIFIED**.
4. **Boundary of B.1:** Freeze interfaces, trust boundaries, threats, negative/fault fixtures, recovery measurement and evidence ownership; do **not** create migrations, change source code, deploy, issue real credentials, assign operator personnel or claim passing G-B01..05. B.2..B.9 each get an independent PR and final-HEAD proof.

## 2. Logical topology and trust boundaries

~~~mermaid
flowchart LR
  User["Browser / authorized operator"] -->|"HTTPS only"| Proxy["Reverse proxy + TLS"]
  Proxy -->|"Private upstream"| API[".NET 10 API workload"]
  Admin["Approved PREPROD admin"] -->|"MFA/privileged path"| CP["Control Plane administrative boundary"]
  API -->|"Durable commands, no selected connection string"| Registry["Control Plane tenant registry + membership + epoch"]
  CP -->|"Approved lifecycle operations"| Registry
  API -->|"Scoped least-privilege identity"| Vault["Dedicated Vault-equivalent boundary"]
  Worker["Independent .NET 10 Worker workload"] -->|"Scoped least-privilege identity"| Vault
  CP -->|"Scoped admin provisioning identity"| Vault
  API -->|"Tenant binding resolved server-side"| DbA[("Tenant A SQL database")]
  API -->|"Tenant binding resolved server-side"| DbB[("Tenant B SQL database")]
  Worker -->|"Revalidated tenant/epoch; durable work"| DbA
  Worker -->|"Revalidated tenant/epoch; durable work"| DbB
  CP -->|"Idempotent create/verify/migrate"| DbA
  CP -->|"Idempotent create/verify/migrate"| DbB
  API -->|"Structured, redacted evidence"| Audit["Independent audit/telemetry boundary"]
  Worker -->|"Structured, redacted evidence"| Audit
  CP -->|"Administrative and security audit"| Audit
  DbA -->|"Encrypted backup chain"| Offsite["Off-host backup vault"]
  DbB -->|"Encrypted backup chain"| Offsite
  Registry -->|"Control Plane backup + recovery metadata"| Offsite
~~~

**B-T0 Public edge:** Only HTTPS reverse-proxy ingress is public. API upstream, private Control Plane administrative endpoints, Worker, SQL Server, Vault, audit sink and backup storage must not be Internet-reachable. AuthN on the edge never substitutes for server resource authorization.

**B-T1 Application identity:** Browser/operator input is untrusted even after TLS. JWT and refresh establish an authenticated session; each API operation still queries authoritative, **current** tenant membership, permission/role, suspension state and revocation epoch. A tenant ID in URL/header/body/token cannot choose a database or create membership.

**B-T2 Control Plane:** Registry holds canonical tenant-to-database binding, lifecycle, approval and security epochs. An independently protected administrative identity controls create/migrate/suspend/decommission. **System Admin has no implicit tenant business-read/write grant**. Business services cannot mutate registry bindings.

**B-T3 Workloads and durable processing:** API and Worker are separate Docker Compose services and principals. Worker receives work by a trusted, durable SQL queue/outbox identity, not arbitrary caller-selected tenant names. It must revalidate current actor, tenant, resource, operation, approval and epoch **at apply/dispatch/retry/CASE resume**, not rely on enqueue-time claims. Distinct scoped SQL credentials per tenant or equivalent verifiably enforced per-DB authorization are required. No worker-wide unfenced system bypass.

**B-T4 Secrets/keys:** Vault or equivalent lives outside API/Worker trust boundary with separated policies and audit for signing, tenant SQL access, encryption and backups. Runtime workload cannot list/read unrelated tenant secrets or Vault administration keys. Failure or missing key/version **fails closed**; do not log values or embed them in Docker images, source, appsettings, environment dumps or traces. Exact product, unseal policy and rotation cadence are B.2 decisions, not Owner-selected facts.

**B-T5 Audit/backup:** Independent immutable or tamper-evident audit and encrypted off-host backups must survive compromised application/tenant DB. A restore snapshot is **not** an authority for revocation state, tenant bindings, key currentness or the external-effect ledger; these require independent recheck and reconciliation before re-enabling operations.

**PREPROD start guard (implementation contract for B.2):** Startup must reject inconsistent environment/mode config or accidentally enabled synthetic auth/worker, development signing keys, unbound public ports, absent TLS/proxy trust, inaccessible Vault, unknown Registry/tenant binding or missing revocation-fence source. Production business endpoints and synthetic diagnostics remain unavailable. A runtime config flag alone cannot authorize live posting. Any failed prerequisite stays **NO_GO** with auditable reason; health/live must not disclose tenants or secrets.

## 3. Authentication, sessions and authorization interface

**Identity boundary (B.3/B.5):** ASP.NET Core Identity for real PREPROD users; JWT access short-lived with explicit issuer/audience/signature/expiry/jti/session subject checks. A separate authorization resolver binds the authenticated **subject → active session → membership → tenant → role/permission policy → resource → current epoch**. JWT tenant or permission claims are untrusted hints unless reconciled against authoritative current state; privilege cannot be inferred from account billing or Control Plane admin status. Require authenticated current ActorId and TenantId for every security-relevant event and audit.

- Refresh token is **opaque, high-entropy, hash-at-rest**, session/family-bound and rotated atomically; use **HttpOnly + Secure** cookie and explicit SameSite/CSRF/origin protections. On old-token reuse, revoke the family/session, advance the authoritative fence and audit the attempted reuse. Logout, suspend, change of permissions, compromised key and account revoke invalidate current authority. No access or refresh tokens in diagnostics/OTel.
- Keep OIDC-ready interfaces (external subject/issuer mapping, identity-store adapter and stable application ActorId), **without claiming a chosen OIDC provider**. The exact access TTL, refresh TTL, algorithm suite, key provenance, cookie SameSite choice and identity UX are B.3 design details requiring review; do not attribute values to the Owner interview.
- Every privilege decision reads or verifies an authoritative epoch newer than stale restored snapshots. Revocation is monotonically fenced in a failure-independent control store. An inaccessible registry/epoch authority **denies action**, including Worker lease claim and CASE/approval re-entry. Fail-closed responses must avoid disclosing peer-tenant existence.
- Enforce **deny-by-default** on tenant, actor, action, object and actual owner. Treat download/export/search, outcome lookup, SQL queries, background dispatch, audit and administrative control separately; no cross-tenant result, metadata hint or writes. Maker, Checker and initiating actor are **distinct identities** wherever the business workflow requires; recheck their current rights at approval and resumption. No self-approval on retry or after restore.

**Proposed minimal security decision envelope (design, not existing code):** ActorId; SessionId; TenantId; ResourceType/Id; Action; MembershipVersion; PermissionEpoch; TenantSecurityEpoch; PolicyVersion/EffectiveAt; RegistryBindingVersion; RequestId/CorrelationId. Server resolves/records authoritative versions at each write boundary. A mismatch means DENY/CASE, **never silently downgrade a missing value to zero**. Exact storage/API shape is finalized in its implementing increment.

## 4. Tenant provisioning and lifecycle contract (B.4/B.5)

**State machine proposed by B.1** (names are APP design, *not* previously chosen by Owner):

~~~mermaid
stateDiagram-v2
  [*] --> REQUESTED
  REQUESTED --> APPROVED: authorized admin approval
  REQUESTED --> REJECTED: rejection, audit
  APPROVED --> PROVISIONING: idempotency key + lease
  PROVISIONING --> VERIFYING: create + migrate completed
  PROVISIONING --> FAILED_RETRYABLE: bounded error
  VERIFYING --> ACTIVE: initial activation gates and fence PASS
  VERIFYING --> FAILED_RETRYABLE: initial verification failed
  FAILED_RETRYABLE --> PROVISIONING: same key, initial provisioning retry only
  ACTIVE --> SUSPENDED: security/admin fence
  SUSPENDED --> REACTIVATION_VERIFYING: fresh approved action, retain suspension fence
  REACTIVATION_VERIFYING --> ACTIVE: current gates and binding/epoch CAS PASS
  REACTIVATION_VERIFYING --> SUSPENDED: any failed check, revoke race or unavailable fence
  ACTIVE --> DECOMMISSIONING: approved retained shutdown
  SUSPENDED --> DECOMMISSIONING: approved retained shutdown
  DECOMMISSIONING --> DECOMMISSIONED: signed/audited archive
~~~

| Transition/operation | Precondition and atomic/idempotent invariant | Failure state |
|---|---|---|
| Request → approve | Registry record + requested seller/business identity; authenticated approved Admin action and immutable approval audit | REQUESTED or REJECTED; no DB accessible |
| Provision/migrate | Registry-scoped operation Id + target binding; private administrative SQL credential; migration ledger replay safe; exclusive lease; cannot choose arbitrary DB from request | FAILED_RETRYABLE, **not ACTIVE**, no routable tenant DB |
| Verify → ACTIVE | Check DB identity/tenant binding/schema hash, access-denied cross-tenant probe, role/epoch authority, secret scope, connectivity, health and audit sink; persist activation evidence and version | stays VERIFYING/FAILED_RETRYABLE |
| ACTIVE → SUSPENDED | Write authoritative lifecycle/epoch fence **before** acknowledging suspend; stop new API writes, Worker claims and replays; record in-flight disposition | no claims of successful suspend before fence commit |
| SUSPENDED → REACTIVATION_VERIFYING → ACTIVE | Fresh authorized admin approval; preserve the SUSPENDED deny fence throughout verification; compare-and-swap current lifecycle/epoch/binding at the final activation decision only after independent isolation, authorization and health checks | Any failed/missing check, revoked authority or CAS conflict returns to **SUSPENDED**, non-routable; no transition into initial PROVISIONING/FAILED_RETRYABLE |
| ACTIVE/SUSPENDED → DECOMMISSIONED | Approved and audited freeze, preserve immutable sources, outbox/inbox/CASE and legal holds; archive/recoverability plan, **never automatic SQL deletion** | DECOMMISSIONING/CASE, no silent purge |

**Reactivation safety invariant (F-01):** `REACTIVATION_VERIFYING` is never routable and cannot claim Worker leases, even when its SQL database is healthy. Initial `VERIFYING → FAILED_RETRYABLE → PROVISIONING` is **only** for never-active first-time provisioning. A reactivation failure or concurrent suspension/revocation must preserve the authoritative SUSPENDED epoch/fence and require a fresh approved attempt; no retry path may silently reset it. B.4/B.5 must implement and independently fault-test the final compare-and-swap, including concurrent revoke and stale Registry/SQL state.

**Binding:** TenantId maps to exactly one registered physical database in an audited, versioned Registry; resource and ActorId bindings must match the registry at every transaction. Guard both ends (Registry mapping and tenant DB binding marker), prevent DNS/connection-string injection and peer swap. Database-per-tenant is not sufficient alone: enforce least-privilege SQL logins, no shared business writer token, query/metadata isolation and tenant-aware backup/restore controls.

## 5. Policy/approval and durable-event boundaries (B.5/B.6)

- L0 is immutable, globally enforced. L1 is tenant-scoped within approved bounds and effective dates; L2 requires CASE + authorized independent Maker/Checker and is **incapable of overriding L0**. Every decision resolves a versioned policy snapshot and immutable approval/source lineage; changed, expired, ambiguous, missing or UNSET policy **denies**. **UNSET ≠ explicitly APPROVED=0**. This contract sets no real price/credit/terms threshold.
- Preserve APP-02A P=(CashHandoverOffered), C=(CashierAcceptanceRequested) and event identity (TenantId,ProducerId,EventId) for **synthetic regression reference only**; PREPROD business enablement requires separately approved real source/actor contracts. Intake ACK remains distinct from applied Business Outcome. Worker must not silently convert UNKNOWN into rejection or issue a new identity; no external exactly-once claim.
- Event/command/inbox/outbox, ledger/source and CASE records are durable and preserved until a separately approved retention/archival policy can lawfully dispose of them. Retry/restore resumes only original stable identity and rechecks current epoch, dependency, policy and immutable sources. A prior external effect uses an independently durable outcome journal before any resending; if unresolved, hold/CASE instead of duplicate effect.

## 6. Secrets, backup, retention and recovery invariants

**Key register:** Required classes: access JWT signing/verification keys; refresh-token hash pepper or equivalent if used; tenant SQL credentials; data-at-rest encryption keys; backup encryption/decryption keys; audit-signing/tamper-evidence keys and external-provider keys (provider use **disabled** for B). Record KeyId, Version, Purpose, PrincipalScope, Activated/Retired/RevokedAt, RecoveryDependency and audit reference **without recording secret material**. A rotation/revocation plan must preserve permitted historical validation/decryption while rejecting revoked sessions; availability is not authorization.

**PITR design (B.7):** SQL Server FULL recovery; daily FULL, differential interval ≤6 h, transaction log backup interval ≤5 min, encrypted off-host copies and verified restore chains. These are **approved targets/configuration requirements**, not observed schedules. PREPROD drill must restore a selected tenant and related Control Plane/Vault dependencies, reconcile its immutable sources/outbox/inbox/external outcomes and compare independent current revocation fences. Until all checks PASS, that tenant stays blocked from business/API/Worker writes.

**Measured targets:** **RPO ≤15 minutes** and **RTO ≤4 hours**. RPO = UTC fault/failure instant minus newest independently witnessed, recovered committed event instant (negative values or absent witnesses invalidate the measurement). RTO = initial outage/security-unavailable instant to first independently verified **safe service restoration**, including tenant binding, key access, auth, fence, SQL invariants, Worker safe re-entry and external reconciliation—not just SQL RESTORE completion. Capture UTC and monotonic duration anchors, backup chain/LSN, recovered witnesses, separate timestamp source, reviewer, TestRunId and errors. A failed/missing witness is **NOT_VERIFIED**, never a synthetic estimate. [Full drill and evidence format](../security/app-02b1-acceptance-evidence.md#3-recovery-experiment-protocol).

**Retention classes:** (1) source/financial/legal and durable APP-02A event evidence, (2) tenant/security/authorization/key/approval audit, (3) controlled recovery/backups, (4) transient diagnostic traces/logs. Legal jurisdiction, durations, archival owner, hold authority, privacy handling and approved purge schedule are all **PENDING explicit owner/legal decision**; a short telemetry TTL can never delete durable source records. Each actual purge requires class-specific legal-hold check, approval, immutable manifest/hash and a witnessed negative unauthorized-purge case. No assumed default legal period.

## 7. Review, handoff and acceptance discipline

| Increment | B.1 contract handoff (implementation/evidence remains future work) |
|---|---|
| B.2 | Linux Compose service/network topology; TLS/private-port guard; Vault policies and denied-wrong-workload tests; PREPROD startup abort on synthetic leakage |
| B.3 | Identity/JWT/cookie/refresh/OIDC adapter, current membership reads, token family reuse/rotation/expiry tests |
| B.4 | Registry schema/versioned binding; approval/state machine; idempotent migration and activation; nondeleting decommission |
| B.5 | Server RBAC/permission/security epochs, API+Worker+CASE recheck and cross-tenant/revocation/restore fences |
| B.6 | Versioned L0/L1/L2, independent Maker/Checker, UNSET/zero split, snapshot/rollback mutation tests |
| B.7 | Backup chain verification, tenant PITR, off-host keys, measured RPO/RTO and external-outcome reconciliation |
| B.8 | Append-only/tamper-evident audit, OTel correlation and alert/incident/retention/hold controls |
| B.9 | Independent physical PREPROD two-tenant G-B01..05 oracle and exact final-HEAD closeout, not production signoff |

B.1's **exit checklist** is: diagrams and trust boundaries reviewed; each threat mapped to owned controls and negative cases; tenant/identity/policy/restore contracts unambiguous and fail closed; exactly **30 named minimum acceptance cases** with fixture ownership and witnesses; pending product/operator/legal decisions visibly unapproved; unchanged Business Baseline and synthetic isolation; independently reviewed docs + current source HEAD Windows CI (historical regression only). **B.1 approval is not G-B01..05 PASS.** If a blocking finding appears or HEAD changes, repeat review and relevant CI.

**Closeout:** B.1 contract **CONTRACT_APPROVED (docs only)** after independent review of the final source and PASS of the same source HEAD's CI ([PR #49](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/49), [closeout](../closeout/2026-11-17-app-02b1.md)). B.2 is the next separately authorized increment, but is **NOT_IMPLEMENTED** and still requires its own review, PREPROD evidence and gates. Original G-STOCK/G-CUT/G-IDEMP/G-LEDGER/G-INV and real AC-11/EV-08 remain NOT_VERIFIED; PREPROD_FOUNDATION_VERIFIED remains NOT_EARNED.
