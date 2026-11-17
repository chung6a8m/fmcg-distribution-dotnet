# APP-02B.1 — PREPROD Security Threat Model and Boundary Review

**State: DESIGN_CONTRACT / REVIEW_PENDING; threat mitigations are NOT_IMPLEMENTED and NOT_VERIFIED.** Scope and authority come solely from [OWN-APP02B-01..10](../decisions/owner-app02b-2026-11-17.md), the [B.1 architecture contract](../roadmap/app-02b1-architecture-security-contract.md), [business constraints](../../business/constraints.md) and [30-case witness matrix](app-02b1-acceptance-evidence.md). Do not confuse review of a proposed control with a tested control. **All G-B01..G-B05 remain NOT_RUN; no mitigation is accepted by this document.**

## 1. Attacker, assets and boundary assumptions

**Protected assets:** physical tenant DB rows and metadata; tenant registry/binding and provisioning approval; sessions, membership and non-rollbackable revocation epochs; signing/encryption/backup keys; immutable business source/ledger/receipt/inbox/outbox/CASE/approval lineage; external-effect outcome evidence; backups/recovery LSNs; audit and legal-hold proofs; CI/source/fixture provenance.

**Adversaries and faults:** unauthenticated public caller; authenticated actor in wrong tenant; stolen/replayed refresh/access token; compromised API container or Worker identity; malicious/overprivileged Control Plane admin; tenant SQL login spill; Vault outage/revoked lease; stale IAM snapshot; interrupted provisioning/backup/restore; delayed/duplicated asynchronous message; corrupted/rewritten audit or test fixture; malicious legal-hold/purge action. Treat all as untrusted until explicitly verified. The model does not assume a real financial channel exists.

**Trust boundary traversal:**

| Boundary | Ingress | Required authority | Expected denied path |
|---|---|---|---|
| B-T0 public HTTPS → API | Browser/network input | Valid TLS, trusted proxy forwarding, rate/size limit; API identity/authorization | Synthetic diagnostic route or private port exposed to Internet |
| B-T1 identity → current entitlement | JWT/cookie, requested resource | Valid signed active session, server-bound membership, current epoch and policy | Token itself picks DB, stale role/refresh grants rights |
| B-T2 Control Plane → Registry/SQL | Approved operator action | Privileged independent Control Plane principal, approval + audit + idempotency | System Admin implicitly reads a tenant business database |
| B-T3 API/Worker → tenant DB | Server-resolved binding; durable work | Correct tenant marker, scoped SQL identity, fenced actor/epoch, source/operation | Cross-tenant query, stale Worker lease, direct connection-string injection |
| B-T4 API/Worker → Vault | Workload identity | Exact-purpose/tenant-scoped policy and current key version | Arbitrary secret read, unseal/admin key access, downgrade to static fallback |
| B-T5 tenant/Registry → audit/off-host backup | Immutable evidence and encrypted copies | Independent append/verify rights, protected encryption/decryption and legal hold | Local compromise rewrites independent witness or resurrects revoked rights |

## 2. Prioritized threat register

**Priority rule:** BLOCKER means implementation of the relevant increment cannot close with the threat unmitigated; HIGH means control is mandatory and requires a negative witness at its owner increment. Every row below is **OPEN / NOT_VERIFIED** today. Threat IDs, not ad-hoc prose, are used in later PR review and incidents.

| ID / priority | Entry and failure/abuse path | Required mitigating contract | Negative/fault witness and implementing owner |
|---|---|---|---|
| TM-01 BLOCKER | Public bypass of proxy/TLS; direct API/Worker/SQL/Vault/Control Plane port | Explicit deny-by-default network, TLS/forwarded-header trust, private management surface; reject synthetic/preprod mismatch | RUNTIME-01, RUNTIME-02 infrastructure negative; **B.2** |
| TM-02 BLOCKER | Dev synthetic JWT/LocalDB worker enabled in PREPROD | Startup strict environment/mode veto; no /api/synthetic/* or /api/diagnostics/* reachable; no local test signing key | RUNTIME-01; **B.2** |
| TM-03 BLOCKER | Forged, expired, wrong-issuer/audience access token or tenant claim tampering | Signature/lifetime/session checks and server-side tenant membership/binding, no fallback DB | B01-02; **B.3/B.5** |
| TM-04 BLOCKER | Refresh token theft/reuse, race of two refresh requests, cookie CSRF | Opaque hashed one-use rotation, family revoke + epoch on reuse, Secure/HttpOnly, origin/CSRF mitigation | B01-02; **B.3/B.5** |
| TM-05 BLOCKER | Authenticated A actor reads/exports B, probes B metadata or selects peer DB | Bind actor+resource+tenant+physical DB with independent peer fingerprint and negative response | B01-01 and B01-04; **B.4/B.5** |
| TM-06 BLOCKER | Stale permission/session or suspend races queued Worker, CASE resume, approval | Check authoritative monotonic epoch/lifecycle at **each** apply/dispatch/retry, fence first, fail closed if source unavailable | B01-03 and B01-06; **B.4/B.5/B.7** |
| TM-07 HIGH | Control Plane admin implicitly gains tenant data access or self-provisions without approval | Separate control identity, admin approval ledger, no implicit tenant business grants | B01-04, B01-05 and RUNTIME-02; **B.4/B.5** |
| TM-08 BLOCKER | Half-provisioned/misbound tenant becomes ACTIVE; retry duplicates or attaches wrong DB | Idempotent provisioning key, versioned Registry binding, verified activation, no orphan routable state | B01-01 and B03-03; **B.4** |
| TM-09 BLOCKER | Maker=Checker, expired policy, L1 overrides L0, UNSET treated as 0 | Independent current actors; immutable L0; scoped effective L1; L2 CASE; UNSET fail closed | B01-05, B02-04, B02-05 and B02-06; **B.5/B.6** |
| TM-10 BLOCKER | API/Worker credential reads other tenant's SQL/Vault material | Workload-scoped SQL least privilege and per-secret Vault policy, audited issuance | B02-01 and B01-01; **B.2/B.4** |
| TM-11 BLOCKER | Vault outage, stale signing/decryption key or revoked version triggers plaintext fallback | No fallback, versioned key registry, explicit verification/recovery window, outbound/Worker hold | B02-02 and B02-03; **B.2/B.3/B.7** |
| TM-12 BLOCKER | Backup theft, corrupt log-chain, missing key or unauthorized restore | Off-host encrypted backup chain/LSN proof, key access separation, checksum and restore denial witness | B03-01 and B03-06; **B.7** |
| TM-13 BLOCKER | Old tenant DB snapshot restores revoked rights or peer Registry binding | Current independent revocation/Registry fence and physical peer test before API/Worker re-entry | B01-06, B03-03 and B03-04; **B.5/B.7** |
| TM-14 BLOCKER | Lost ACK/external UNKNOWN after restore or duplicated message causes repeat financial effect | Independent durable outcome journal; original MessageId/EventId; reconcile/CASE, no blind resend, no exactly-once promise | B03-05 and B04-02; **B.7/B.8** |
| TM-15 HIGH | Admin/compromised app edits or purges audit, conceals alert, bypasses hold | Append-only tamper-evident audit, protected sink, alert delivery/incident chain, legal-hold proof | B04-01, B04-03, B04-04, B04-05, B04-06; **B.8** |
| TM-16 BLOCKER | False PASS from API self-attestation, altered expected oracle or CI on stale HEAD | Independent two-physical-tenant witnesses, frozen expected/diff/mutation, distinct reviewer, exact final-HEAD CI | B05-01..B05-06; **B.9** |

**RUNTIME-01/RUNTIME-02** are additional B.2 engineering checks, not two of the 30 B.9 frozen group cases. Their exact tests are specified below. No threat is closed by listing it in a table.

## 3. Failure ordering and concurrency rules

~~~mermaid
sequenceDiagram
  participant API as API or Admin
  participant CP as Current Registry / epoch fence
  participant SQL as Tenant SQL
  participant W as Worker
  API->>CP: Authenticate, verify membership+current permission+tenant state
  CP-->>API: Identity/epoch/binding version or DENY
  API->>SQL: Conditional source+outbox transaction under current fence
  SQL-->>API: Durable COMMIT or rollback
  SQL-->>W: Discover committed durable work
  W->>CP: Revalidate current epoch, lifecycle, actor/resource/policy
  alt Current entitlement and source
    CP-->>W: Valid at apply instant
    W->>SQL: Apply only with conditional version/fence check and stable identity
  else Revoked, unknown, restored or CP unavailable
    CP-->>W: DENY / unavailable
    W->>SQL: Hold/CASE (no effect, no new identity)
  end
~~~

The check-then-apply gap requires an enforceable **linearization/fence condition**, not merely two independent reads: a revocation committed before a write's authorization fence must win; tests must inject suspension/role revoke between claim, verify and SQL COMMIT. Implementers must freeze their transaction/protocol so a stale authorization result cannot commit after a later epoch. For external dispatch, an unverified effect journal or ambiguous acknowledgement is a mandatory hold/CASE and escalation, never automatic new-message retry.

**Representative faults required at later increments:** double refresh race; token reuse after rotation; stale Worker lease after suspend; wrong tenant DB connection; provisioning interrupted before and after migrations; Vault outage during SQL open; key retirement with archived data; SQL log-backup gap; tenant A snapshot before revoke; corrupted audit hash; alert drop; legal hold on purge; changed oracle expected value; changed final commit after review. Each needs precondition → injected fault → independently observed effects/no-effects → evidence reference. Successful HTTP alone is insufficient.

## 4. Test fixture and account ownership (design, not existing provisioned users)

| Fixture ID | Boundary and purpose | Credential/data handling |
|---|---|---|
| TF-TEN-A / TF-TEN-B | **Two different physical PREPROD SQL databases**, registry-bound to distinct TenantId/Server-side seller; one peer stays untouched during each negative | Created afresh per test run, no real customer/payment data, separate test scopes; capture DB name/schema version/fingerprint only |
| TF-A-SALES / TF-A-CASHIER | Distinct scoped tenant A subjects for the event/approval negatives | Issued by PREPROD Identity in B.3, not existing synthetic test JWT |
| TF-A-MAKER / TF-A-CHECKER | Independent actor IDs/roles to forbid self-approval and initiator=Checker | Track authority and epoch without logging token |
| TF-B-USER | Active tenant B subject with no A membership | Must never obtain A data via API, query/export or Worker |
| TF-SYSADMIN | Control Plane system administrator **without tenant data grants** | Private administrative boundary, no implicit tenant SQL read |
| TF-REVOKED | Tenant A subject whose current membership/epoch is revoked **after** token issue and before queue/restore | Current independent revocation store always newer than restored snapshot |
| TF-KEY-OLD/NEW | Synthetic **PREPROD** versioned signing/backup test keys with valid and revoked versions | Secrets manager only, record key IDs and hashes, no private key material in evidence |
| TF-OUTCOME | Synthetic external-effect surrogate with durable received/unknown/already-applied outcomes | No actual invoice, bank, accounting or cashier provider |

**Responsible implementation function** (names not appointed individuals): Platform/Runtime engineer owns B.2 network/Vault/startup; IAM engineer B.3 sessions; Tenant/DB engineer B.4 provisioning and B.7 restore; Security/Authorization engineer B.5; Policy/Business reviewer B.6; Operations/Audit engineer B.8; independent security+DB evidence reviewer B.9. Every case records named actual Owner and independent Reviewer at execution, even if one person fills multiple implementation functions; **self-approval of security evidence is not independent review**. This contract does not assign personnel or grant their permissions.

**B.2 additional negative acceptance (outside B.9's 30 minimum cases):**

- RUNTIME-01: In PREPROD start with SyntheticAuth:Enabled/App02Worker:Enabled, dev key, or public management DB/Worker port -> startup/health admission fails and all synthetic routes stay unreachable; inspect network and service logs with no secret values.
- RUNTIME-02: Bypass reverse proxy, forge forwarded headers, access Control Plane from general public ingress, or deny workload Vault policy -> no management/API privilege and audit captures a redacted denial.

## 5. Open design dependencies requiring explicit later review

| Item (not approved by Owner interview) | Decision owner increment | No silent default |
|---|---|---|
| Linux provider, subnet/firewall, reverse proxy/TLS termination and cert ownership | B.2 | No public management/SQL/Vault port; not deployment authority |
| Vault product/version, unseal/recovery, rotation cadence and workload credential binding | B.2 | No hardcoded/exposed secret, absent Vault blocks activation |
| Signing algorithm, access/refresh TTL, SameSite/CSRF design, OIDC provider | B.3 | No reuse of synthetic JWT auth or unconstrained refresh |
| Registry storage/provider, migration fencing and binding version semantics | B.4/B.5 | No activation before verification or suspend race |
| Policy numeric thresholds, credit/terms, exact real Maker/Checker organization | B.6 and separate Owner/business authority | **UNSET**, real business operation denied |
| Backup storage vendor, retention periods, legal jurisdiction/hold authority, service operators, restore drill timing | B.7/B.8 | No guessed legal retention; no unmeasured RPO/RTO PASS |
| Real external invoice/accounting or business channel | **outside B; Owner authorization required** | B_NO_CHANNEL BLOCKED, no live effect |

**Closure:** B.1 may approve this threat model and acceptance obligations, not mitigation. At B.2..B.9 a blocker without independent negative witness remains an explicit NO_GO; APP-02C/D/E, all production P0 and real EV-08 remain outside this authorization.
