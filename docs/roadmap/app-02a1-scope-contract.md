# APP-02A.1 — Owner Scope & AC-11/C→P Dependency/Recovery Contract

**State (2026-11-17): PROPOSED / DOCUMENTATION_PREPARED / OWNER_AUTHORIZATION_PENDING.** This is the proposed **first APP-02 increment** and an implementation handoff, not an Owner-approved decision or a completed AC-11 test. No source code, SQL migration, API, outbox worker, business oracle, deployment or actual external evidence is changed by this plan. Parent: [APP-02 roadmap](app-02.md).

## 1. Why this increment exists

APP-01E proved bounded synthetic command rollback/retry, two-client contention, post-COMMIT lost ACK and restart recovery and T0–T6 reconciliation across two nonce-owned LocalDB tenants. It did **not** establish the complete [AC-11](../../business/acceptance.md) dependency rule: *event C depends on event P*, missing/reordered dependency must remain durable and a safe retry cannot multiply business effects. Normative requirements: [BR-16](../../business/business-rules.md), [POL-16](../../business/policies.md), [AC-11](../../business/acceptance.md), EV-08; also AC-01 for tenant isolation and AC-13 for restore/revocation boundaries where relevant.

**Key distinction:** durable intake ACK, durable business application and downstream external acknowledgment are separate facts. Network failure is UNKNOWN, not business REJECTED. "Exactly once" means **exactly one persisted source/business effect for a canonical identity**, even if transport delivery is at least once; no exactly-once network delivery assumption.

## 2. Decisions that must be closed by Owner before A.2

| Candidate ID (not an approved decision) | Open question | Proposed safe default for review | Blocking effect |
|---|---|---|---|
| **OWN-APP02-01** | Authorize APP-02A.1–A.4 synthetic-only first, and independently gate APP-02B–E? | **Authorize a separate nonproduction AC-11 slice first**; all production work requires additional Owner decision | **APP-02 remains NOT_AUTHORIZED** until explicitly accepted |
| **OWN-APP02-02** | What do C and P concretely represent (event kind, origin, order, producer, source, dependency cardinality)? | Do **not** infer from short letters or substitute APP-01 command replay; bind business source event IDs and permitted dependency order | Cannot freeze event schema / expected oracle |
| **OWN-APP02-03** | Event identity, actor/tenant ownership, payload normalization and hash version? | Preserve APP-01 canonical tenant+producer+ID uniqueness where semantically compatible; separate consumer receipt ID from business source ID | Cannot define durable conflict/replay correctness |
| **OWN-APP02-04** | When is a receipt ACK permitted? How do RECEIVED, PENDING, APPLIED, REJECTED, UNKNOWN and CASE differ? | ACK only after durable intake COMMIT; APPLIED only after source/effect COMMIT; no UNKNOWN→REJECTED inference | Cannot implement retry or outcome lookup safely |
| **OWN-APP02-05** | Missing P/expired dependency, timeout, retry budget, retention and case owner? | Fail closed; keep pending/case durably, observable with asOf/next action; no silent TTL-drop | Cannot implement recovery, DLQ or retention |
| **OWN-APP02-06** | Conflict, dead-letter/case closure and manual correction authorization? | No automatic effect rewrite; maker/checker or audited correction as business requires; immutable lineage | Cannot automate reconciliation of exceptional state |
| **OWN-APP02-07** | Outbound delivery destination and effect acknowledgment boundary? | No real external provider, invoice or accounting side effect in synthetic increment; validate outbox local protocol first | External delivery design remains future gated work |
| **OWN-APP02-08** | Which new evidence and P0 criteria are requested beyond AC-11? | Separate synthetic APP-02A.4 verification from EV-08 actual environment and production G-IDEMP | Do not inflate synthetic evidence into EV/P0 acceptance |

Each decision record must contain **chosen value, scope, effective status, unresolved blockers, AC/BR/POL/EV traceability and Owner disposition**. Merely reviewing/merging this file does not imply the Owner has approved any candidate.

## 3. Candidate behavioral contract (to be refined, not yet frozen)

### Envelope

A durable intake event should be independently attributable to a **server-bound TenantId**, signed/validated actor, producer, stable event identity, event kind, payload/hash version, immutable source references and optional explicit prerequisite identity/edge. Separate `occurredAt` from `receivedAtUtc`; timestamps do not prove business eligibility or dependency completion. Avoid client-provided tenant routing authority. Decide whether source commands and events share an identity namespace **before** schema approval.

Potential lifecycle: `RECEIVED` (durably accepted but not business-posted) → `PENDING_DEPENDENCY` (C waits for P) → `APPLIED` or a reasoned `REJECTED`/owned `CASE` as the contract permits. `UNKNOWN` is an **observer/outcome-lookup state**, not a persisted business rejection. An inbound HTTP 200/202 by itself is not an APPLIED ledger witness. Pending age/queue backlog must be visible with asOf, n/N, case owner and next action.

### Invariants

1. **One identity, one normalized payload, one outcome:** replay of identical identity/content returns the saved state; changed content under that identity produces conflict with **zero added effects**. No hidden new event identity on timeout.
2. **Dependency before effect:** C with missing/wrong-tenant/not-yet-durable P may be safely retained pending, but cannot post an APP-01 source/ledger effect early. When P becomes valid, processing C is resumed idempotently.
3. **Atomic business apply:** any source, rights/stock/AR/cash/ledger effects and durable completion mark must commit atomically. Rollback means no partial economic or identity state except a separately committed intake/pending record allowed by the frozen contract.
4. **Tenant/actor isolation:** both intake, dependency lookup, consumption and outcome reads are tenant/actor scoped. Opposite physical tenant is unchanged after any local journey.
5. **Crash-safe replay:** ack loss, server/worker restart or 2-worker SQL contention cannot drop the accepted C or post twice. Application recovery reads durable SQL state, not just HTTP responses or in-memory flags.
6. **Cases and external reality:** an occurred real-world event is not silently discarded when late; represent exception/case with owner and reconciliation. External effects are out of synthetic scope and cannot be presumed reversible or repeated.
7. **No scope inflation:** `SYNTHETIC_UNITLESS`, `invoiceEligible=false`, real policy UNSET, offline write disabled, B_NO_CHANNEL BLOCKED; the immutable Business Baseline stays the authority for expected outcomes.

### Candidate physical design for A.2 (NOT IMPLEMENTED)

- Durable inbox **receipt** keyed by canonical event identity/hash, tenant-bound actor/source lineage and monotonic state history (or auditable transition records).
- Explicit dependency edge(s) with P identity and guarded tenant semantics; independent constraint prevents C/P cross-tenant joins and unexpected cycle paths if the approved model allows multiple edges.
- Transactional outbox record tied to committed domain source, with durable delivery attempt/ACK state; outbound processing is separate from primary business source posting.
- Recovery/case journal for terminal policy rejection, unresolved dependency, retry disposition, case ownership and source-linked correction.
- Existing APP-01 `Command`/`BusinessSource`/`SourceLine`/`LedgerEffect` authority must be reused or explicitly bridged; **no competing accounting truth** and no unguarded duplicate source/effects.

These are conceptual entities, not table names, a migration list or an instruction to insert a production transport.

## 4. Proposed negative/fault acceptance and SQL witnesses

| ID | Given / fault | Expected actual witness from SQL | Rejected outcome |
|---|---|---|---|
| A11-01 | P committed, C follows | P and C source/line/dependency lineage, one C economic effect | Duplicate C effect |
| A11-02 | C accepted before P | C durable `PENDING_DEPENDENCY`, no premature source posting; exactly one apply after P | C dropped or applied early |
| A11-03 | P absent / invalid / wrong tenant | Pending or reasoned terminal state per approved policy; no cross-tenant read/write | Guessing P, effect in peer tenant |
| A11-04 | Same producer+event ID, same payload | Stable receipt/outcome/hash and invariant all-user-table fingerprint on replay where applicable | New source/ledger rows |
| A11-05 | Same identity, changed payload/kind | Conflict with old outcome and unchanged user-table digest | Changed committed source/effect |
| A11-06 | Two consumers race on pending C/P | Observed SQL contention/ordered recovery, one posted effect | Lost C, duplicate apply, deadlock masking |
| A11-07 | SQL failure after one or more effects before COMMIT | Full business transaction rollback, recoverable same-key retry | Partial source/rights/ledger |
| A11-08 | ACK lost after intake COMMIT and/or after apply COMMIT | Outcome lookup separates durable RECEIVED/PENDING/APPLIED from client UNKNOWN; same-key recovery | Fresh identity or false rejection |
| A11-09 | Server/worker process restarted with pending C | Different OS process reloads pending SQL and resumes when P appears | In-memory-only dependency loss |
| A11-10 | Actor revoked / different tenant retries | Denied without resurrecting rights or cross-tenant changes | Old JWT/peer tenant accepted |
| A11-11 | Dependency unresolved beyond approved threshold | Observable pending/case with asOf, owner, next action and explicit policy disposition | Auto-drop or fabricated `0` |
| A11-12 | Independent reconciliation after replay/recovery | Source/line/effect uniqueness, physical/credit/AR/cash conservation as relevant; signed expected/actual/diff | Self-certified HTTP-only success |

These are **candidate scenario IDs**, not new immutable business AC IDs or claims of executed tests. A.1 must specify concrete expected events, permitted state transitions, payloads and outputs before A.4 asserts the oracle.

## 5. Work split after the authorization gate

- **A.1 (this documentation-only proposal):** formalize scope and decision register, approve event mapping, protocol, state transitions, failure matrix, oracle/evidence contracts, safety exclusions and review ownership. No migrations.
- **A.2 (separate PR):** scoped per-tenant migration/schema with FK/unique/immutability constraints, no posting behavior; fresh/repeat SQL LocalDB two-tenant tests and independent negative data tests.
- **A.3 (separate PR):** service/worker/inbox/outbox command processing, pending dependency admission/release, safe exactly-once source posting under at-least-once delivery, tested concurrency and restart. No production endpoint or external provider integration without separate approval.
- **A.4 (separate PR):** independent SQL readback oracle, two tenants, mismatch sentinels, all-user-table hashes, replay/conflict/rollback/lost-ACK/process restart evidence with source/workflow/actor/tenant TestRunId, script that fails closed on missing cells/provenance; preserve APP-01E.5 **247/247** historical suite and all previous gates. New test floor is determined only after A.2–A.4 scenarios are frozen.

## 6. Review / Definition of Ready for A.2

A.1 may be marked **CONTRACT_APPROVED** only when all of the following are explicitly recorded: (a) independent docs review and no unresolved findings, (b) Owner authorization specifying synthetic scope and deferred production scope, (c) concrete C/P mapping including permitted dependencies, (d) state/ACK/identity/hash/retry/case rules with clear owner and refusal modes, (e) negative/fault matrix with expected and SQL witness sources, (f) immutable baseline and prior regression gates, (g) security/tenant boundary and no production/EV/P0/pilot assumption.

If anything remains missing, leave **APP-02A.1 PROPOSED / OWNER_AUTHORIZATION_PENDING** and **APP-02A.2 BLOCKED**. Do not label documentation review as AC-11 verified.

**Reference index:** [APP-02 roadmap](app-02.md) · [APP-00–APP-01 historical closeout](app-00-app-01.md) · [Business Acceptance](../../business/acceptance.md) · [Owner APP-00/01 authority](../decisions/owner-app-2026-11-15.md).
