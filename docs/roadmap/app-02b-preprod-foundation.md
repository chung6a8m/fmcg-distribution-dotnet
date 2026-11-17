# APP-02B — PREPROD Production-grade Foundation: implementation and independent gates

**State as of 2026-11-17: OWNER_APPROVED (OWN-APP02B-01..10) / B.1 CONTRACT_APPROVED (DOCS_ONLY) / B.2 IMPLEMENTED_IN_DRAFT_PR #50 / PREPROD_RUNTIME_NOT_VERIFIED / B.3–B.9 NOT_IMPLEMENTED / G-B01..G-B05 NOT_RUN.** The nine APP-02B increments are authorized **solely in PREPROD/nonproduction**, not as production deployment, A/B/Mixed mode admission, real business posting, pilot, or tenant go-live. [Decision register](../decisions/owner-app02b-2026-11-17.md) · [APP-02 program roadmap](app-02.md).

## 1. Starting baseline and frozen boundaries

- APP-02A.1–A.4 are merged and **AC11_SYNTHETIC_VERIFIED only** on two synthetic nonproduction tenant LocalDBs ([A.4 closeout](../closeout/2026-11-17-app-02a4.md)). No real-environment AC-11/EV-08 or five production P0 gates are verified.
- Existing .NET 10 / ASP.NET Core + Dapper + FluentMigrator + React/Vite PWA, SQL Server database-per-tenant and event/inbox/outbox/APP-01 source ledger are **reference and regression baselines**; do not present signed synthetic JWT/LocalDB guards as real authentication or production authorization.
- Linux VPS/VM, Docker Compose, separate API and Worker, HTTPS reverse proxy, private SQL Server; ASP.NET Core Identity, short-lived JWT, opaque rotating/revocable refresh, OIDC-ready; admin-approved tenant provisioning; RBAC/permission epoch; versioned L0/L1/L2 policies; dedicated Vault-equivalent; PITR; append-only audit/OTel; nine PRs/five gates.
- `business/` and `business.lock.json` remain read-only. `SYNTHETIC_UNITLESS`, `invoiceEligible=false`, offline business writes disabled, `B_NO_CHANNEL BLOCKED`, real policy/price/credit/terms `UNSET`, no live external invoice/accounting. 125% browser-scale exception is historical `EXCEPTION_ACCEPTED / NOT_TESTED`, not a new APP-02B gate.

## 2. Nine sequential, independently reviewable increments

| Increment / PR | Delivery scope and expected evidence | Exit state after its own independent final-HEAD gate |
|---|---|---|
| **[APP-02B.1](app-02b1-architecture-security-contract.md)** — Architecture & Security Contract | Deployment/trust-boundary diagrams, API/Worker/Control Plane and Vault threat model, tenant identity and provisioning state machine, token/key/backup/retention boundaries, prioritized negative/fault/restore matrices, precise test fixtures/ownership and explicit PREPROD-only guard. Review target RPO<=15m/RTO<=4h; don't invent legal retention or real credit thresholds. [Threat model](../security/app-02b1-threat-model.md); [30-case evidence matrix](../security/app-02b1-acceptance-evidence.md). | `CONTRACT_APPROVED` (docs only), otherwise blocked |
| **[APP-02B.2](app-02b2-preprod-runtime.md)** — PREPROD Runtime & Secrets Foundation | Reproducible Linux Docker Compose deployment with API and Worker split, HTTPS reverse proxy, private SQL, separated Vault-equivalent trust boundary, least-privilege identities/secret access, TLS failures and rotation/fail-closed probes; no reachable synthetic-only privileged endpoints. | `PREPROD_RUNTIME_VERIFIED` with deployment, network and secret-negative evidence |
| **APP-02B.3** — Identity & Sessions | ASP.NET Core Identity, JWT access validation, opaque refresh-token hashing/rotation/reuse detection/revoke, HttpOnly/Secure refresh cookie, OIDC-ready separation; real PREPROD identities, no synthetic JWT role escalation; signed/current actor/tenant lookup. | `IDENTITY_VERIFIED` with negative JWT/replay/session tests |
| **APP-02B.4** — Tenant Registry & Provisioning | Admin approval, control-plane registry, database-per-tenant create/bind/migrate with idempotent retries, pre-activation schema/isolation/auth/health checks, suspension/reactivation and audited nondeleting decommission; cross-tenant and partial-fault negatives. | `TENANT_LIFECYCLE_VERIFIED` |
| **APP-02B.5** — Authorization & Revocation | Deny-by-default RBAC, tenant/resource policies, privileged Control Plane separated from business data; maker/checker identity, permission/security epochs and fence; API/Worker/queued work revalidation; restore/replay must never resurrect revoked rights. | `AUTHORIZATION_VERIFIED` with independently observed two-tenant SQL/HTTP negatives |
| **APP-02B.6** — Policy Registry & Approvals | Versioned L0 immutable, bounded tenant L1, approved L2 CASE; effective date/scope/version, immutable Maker/Checker approval and transaction snapshots; `UNSET` distinct from explicitly `APPROVED=0`, fail-closed, source lineage and change/replay tests. No real values approved by this PR. | `POLICY_GOVERNANCE_VERIFIED` |
| **APP-02B.7** — Backup / PITR / Safe Restore | SQL FULL recovery, daily full, differential <=6h, transaction logs <=5m, encrypted off-host copies; tenant-scoped restore plus Control Plane/Vault recovery; real measured PREPROD drill RPO<=15m/RTO<=4h; external-effect reconciliation, revocation/no-resurrection barriers and fail-closed unknown outcome handling. | `PREPROD_RECOVERY_VERIFIED` only if actual drill meets both targets |
| **APP-02B.8** — Immutable Audit & Operations | Append-only tamper-evident tenant/actor/source/policy lineage, OTel traces/logs/metrics across API/Worker/SQL/inbox/outbox, alerts, incident severity/ownership/runbook/escalation, classified telemetry vs business/legal retention, hold and approved archive/purge paths. No arbitrary APP-02A TTL. | `AUDIT_OPERATIONS_VERIFIED` with alert/incident/tamper/retention evidence |
| **APP-02B.9** — Independent PREPROD Oracle & Closeout | Independent two-physical-tenant PREPROD matrix for G-B01..05, actual-vs-expected SQL/HTTP/security/restore/telemetry witnesses, peer-tenant no-write/read, fault/restart/revocation/key-rotation, reproducible TestRunId/build/fixture SHA, exact final-HEAD CI, independent review; preserve APP-01E/APP-02A/frontend/PWA/Playwright regressions. | `PREPROD_FOUNDATION_VERIFIED` **only if all five gates PASS**; otherwise `NOT_VERIFIED` |

**Delivery order:** B.1 -> B.2 -> B.3 -> B.4 -> B.5 -> B.6 -> B.7 -> B.8 -> B.9. Each is a **separate PR** with independently proven exit; no partial PR may label the final program verified. This Owner decisions/documentation PR is a planning artifact **outside** the nine implementation PRs.

## 3. Independent acceptance matrix

| Gate | Required proof | Negative/rollback/recovery assertion | Current |
|---|---|---|---|
| **G-B01 — Security & Tenant Isolation** | Distinct physical tenant DBs, real PREPROD actor identity/membership, roles and permission epoch checked on API + Worker + resume/retry | Peer-tenant full no-read/no-write fingerprint, token/refresh replay denial, privileged System Admin no implicit data access, Maker/Checker separation and no restored revocation | **NOT_RUN** |
| **G-B02 — Secrets & Policy Governance** | Measured Vault access/key version/rotation/revocation; policy history, effective dates, snapshots, L0/L1/L2 approvals | Wrong identity/stale secret/secret outage fail closed; `UNSET` cannot act as zero-approved; self-check and policy mutation denied; no lost decryption capability for valid history | **NOT_RUN** |
| **G-B03 — Backup & Recovery** | Backup chain + off-host encryption witness, tenant & Control Plane/Vault restored state, actual RPO/RTO measurements and reconciliation report | Snapshot cannot resurrect rights, tenant binding cannot point to peer data, `UNKNOWN` outside effect does not trigger new-ID resend; halt or CASE when reconciliation unavailable | **NOT_RUN** |
| **G-B04 — Operations & Audit** | Tamper-evident audit, correlated span/log/metric, alert trigger/reception and escalation evidence, retention/legal hold behavior | Missing/falsified lineage detectable; failed alert/incident handoff visible; no unauthorized purge, no automatic TTL loss of APP-02A durable records | **NOT_RUN** |
| **G-B05 — Independent Final-HEAD Acceptance** | Review on exact final source HEAD, final CI PASS with 0 failed/skipped required tests, two physical tenant reports, TestRunId and artifact/source lineage; original regression suites remain green | Mutated expected/witness/peer state causes hard FAIL; no PASS from API self-attestation or superseded HEAD; unresolved blocking findings prevent merge | **NOT_RUN** |

**Frozen B.1 design obligations (not executed):** [B.1 evidence contract](../security/app-02b1-acceptance-evidence.md) specifies **30 minimum named acceptance cases** (6 per G-B01..05), independent expected/actual/difference/peer fingerprints, fixture ownership, mutation negatives, recovery witness/LSN protocol and RPO<=15m/RTO<=4h measurement. B.2..B.9 must freeze implementation-specific fault tolerances and artifacts at their own handoffs; **all 30 cases and five gates are NOT_RUN**. No actual success is implied.

## 4. Non-negotiable cross-cutting failure modes

- **Server authority:** TenantId never selects an arbitrary database supplied by a caller; a token accepted for one tenant is no authority for another; current role/epoch required at operation, worker apply and CASE/approval time.
- **Provisioning safety:** Idempotent create/retry must not leave an accessible half-provisioned database; suspended tenants cannot route business writes even with a previously valid JWT or in-flight job.
- **Secrets and identity:** Sensitive values cannot be checked into Git, images, plaintext deployment configs or telemetry; token signing-key rotation preserves valid verification window without re-enabling revoked tokens.
- **Recovery correctness:** Keep an independently reliable revocation fence and external-effect durable outcome journal across restores. If verification is impossible, block reactivation/dispatch until explicit reconciled disposition; no external exactly-once claim.
- **Business authority:** APP-02B does not enable real-money movements, stock writer/cutover, invoice/channel, production policy or live B sales. A.4's synthetic APP-01D bridge remains a synthetic-only result.
- **Evidence:** Retain previous APP-01E/APP-02A CI evidence and regressions; separately collect APP-02B PREPROD build/environment/DB/actor/correlation/backup/restore evidence. TestRunId and reviewed final HEAD must match; changing HEAD reopens all sign-off checks.

## 5. Status ledger and next PR

| Item | Status at decision closeout |
|---|---|
| OWN-APP02B-01..10 | **OWNER_APPROVED (10/10)** — scope only |
| APP-02B.1..B.9 runtime implementation | **B.2 IMPLEMENTED_IN_DRAFT_PR #50 / PREPROD NOT_VERIFIED; B.3–B.9 NOT_IMPLEMENTED** |
| APP-02B.1 docs-only design contract | **CONTRACT_APPROVED / DOCS_ONLY** — independent review and final-HEAD CI; no PREPROD gate evidence |
| G-B01..G-B05 | **NOT_RUN / NOT_VERIFIED** |
| Potential final label `PREPROD_FOUNDATION_VERIFIED` | **CONDITIONAL, NOT_EARNED** |
| Full real-environment AC-11, actual EV-08, EV-01..15 | **NOT_VERIFIED** |
| Five original production P0 gates | **NOT_VERIFIED / NON-WAIVABLE** |
| APP-02C/D/E, production, A/B/Mixed mode admission, pilot, go-live | **NOT_AUTHORIZED** |

**APP-02B.2 implementation handoff:** [draft PR #50](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/50) isolates PREPROD API/Worker, HTTPS/Caddy and scoped Vault probes with [RUNTIME-01/02 requirements](app-02b2-preprod-runtime.md). CI is not physical PREPROD evidence; RUNTIME-01/02 remain **PREPROD_NOT_VERIFIED** pending deployed Vault/network fault witnesses and independent review; no tenant business admission.

**APP-02B.1 design closeout: CONTRACT_APPROVED (DOCS_ONLY)** after independent review and final-HEAD CI, [PR #49](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/49) / [closeout](../closeout/2026-11-17-app-02b1.md). The [architecture contract](app-02b1-architecture-security-contract.md), [threat model](../security/app-02b1-threat-model.md) and [30-case matrix](../security/app-02b1-acceptance-evidence.md) freeze future obligations, **not implementation or gates**. **APP-02B.2 — PREPROD Runtime & Secrets Foundation is next, NOT_IMPLEMENTED**.

References: [APP-02 roadmap](app-02.md) · [Owner decisions](../decisions/owner-app02b-2026-11-17.md) · [Business constraints](../../business/constraints.md) · [AC-01/04/11/13/16](../../business/acceptance.md) · [previous synthetic evidence](../closeout/2026-11-17-app-02a4.md).
