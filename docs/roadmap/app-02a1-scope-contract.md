# APP-02A.1 — Owner Scope & AC-11/C→P Dependency/Recovery Contract

**State (2026-11-17): OWNER_DECISIONS_APPROVED / DOCUMENTATION_REVIEW_PENDING / NOT_IMPLEMENTED.** The Owner explicitly authorized **APP-02A.1–A.4 synthetic/nonproduction only** in [OWN-APP02-01..08](../decisions/owner-app02-2026-11-17.md). This document freezes the **Owner-selected contract** for review; it is **not** `CONTRACT_APPROVED` until an independent documentation review and final-head gates pass, and is not an AC-11 test result. APP-02B–E, production integrations, EV/P0, pilot and go-live remain **NOT_AUTHORIZED / NOT_VERIFIED**. Parent: [APP-02 roadmap](app-02.md).

## 1. Authority, traceability and baseline

The separately reviewed APP-01E.5 T0–T6 LocalDB closeout proved bounded synthetic command retry, rollback, loss of HTTP ACK, OS restart and cross-transaction reconciliation, **not** dependency processing C→P. Normative immutable requirements: [AC-11](../../business/acceptance.md), [BR-16](../../business/business-rules.md), [POL-16](../../business/policies.md), EV-08 (external evidence pending). Related: AC-01 (tenancy), AC-10/21 and BR/POL-13 (cash handover), AC-13 / BR/POL-17 (revocation/recovery) and AC-15 / BR/POL-19 (source/outcome/reconciliation).

Selected Owner dispositions:

| ID | Approved contract | Details |
|---|---|---|
| OWN-APP02-01 | APP-02A.1–A.4 synthetic-only authorization; B–E require new approval | [Owner record](../decisions/owner-app02-2026-11-17.md) |
| OWN-APP02-02 | `P=CashHandoverOffered`, `C=CashierAcceptanceRequested`, one P → at most one successful C | §2 |
| OWN-APP02-03 | Separate `(TenantId,ProducerId,EventId)` event identity, versioned SHA-256, separate receipt/source IDs | §3 |
| OWN-APP02-04 | Durable intake ACK separate from business outcome, distinct lifecycle / UNKNOWN | §4 |
| OWN-APP02-05 | Event-driven; safety scan 15 minutes; six technical retries; 24-hour dependency CASE; no auto-delete | §5 |
| OWN-APP02-06 | Independent Maker/Checker; same-ID resume, immutable correction source and audit | §6 |
| OWN-APP02-07 | Transactional Outbox + durable synthetic consumer; at-least-once, stable MessageId | §7 |
| OWN-APP02-08 | Twelve independent SQL evidence scenario groups; two physical tenants; strict final-head CI/review; EV/P0 separate | §8 |

All eight are **OWNER_APPROVED as decisions**, not `AC11_SYNTHETIC_VERIFIED`. Approved safety boundaries: immutable `business/` tree / `business.lock.json`, `SYNTHETIC_UNITLESS`, `invoiceEligible=false`, development/integration-test-only signed synthetic JWT and nonce-owned LocalDB, no offline business writes, `B_NO_CHANNEL` blocked, real financial/credit/invoice policy **UNSET**.

## 2. Concrete C/P mapping, meaning and source of truth

| Field | Prerequisite P | Dependent C |
|---|---|---|
| Event kind | `CashHandoverOffered` | `CashierAcceptanceRequested` |
| Originator | Authenticated **Sales** producer/actor | Authenticated **Cashier** producer/actor, independent of Sales |
| APP-01D mapping | Immutable non-posting `CashHandoverOffer` / OFFER `BusinessSource` | APP-01D `CashHandoverAcceptance` with independent `CashierCountEvidence`, linked to exactly one offer |
| Durable requirement | P itself persisted and verified in same tenant with exact Trip, offer amount and source identity | May be durably received **before** P, with stable reference to the intended P; no acceptance or financial effect until valid P and count witness |
| Business effect | **None** on OFFER (no custody/fund/AR/COGS) | **One** acceptance posting, **two** exactly balanced ledger legs (`CUSTODY-OUT` and `FUND-IN`), **no second AR/receipt allocation** |
| Cardinality | One P can have at most **one successful** acceptance | Each C names **one** prerequisite P; different C event IDs cannot accept the same P twice |

C must carry a *stable canonical reference* to the intended P identity even when P has not yet been persisted. A.2 schema must specify the event→APP-01D offer/source/command correlation and the independent count-evidence reference without creating a phantom P, pre-seeding a real-world cash fact, or trusting an unsourced client flag. P with same-trip/amount but a different identity is **not** an authorized replacement dependency. Existing APP-01D cash-handover logic and source/ledger authority are reused/bridged, never shadow-posted by a new ledger.

## 3. Event identity, envelope and canonical replay

Canonical uniqueness is **`(server-bound TenantId, ProducerId, EventId)`**, with an event namespace **separate from APP-01 `BusinessCommand`**. `EventKind` and payload hash are **not** elements of the uniqueness key. Stable `ReceiptId` is distinct from `EventId` and `BusinessSourceId`; the latter is the posted APP-01 source, not an intake acknowledgment. `ProducerId` identifies the sender source system; `ActorId` is taken from a validated signed synthetic JWT, and the initiating actor plus event source lineage are immutable. Tenant and effective permissions are server-derived, never caller-chosen connection strings or tenant routing.

Minimum envelope: `ProducerId`, `EventId`, `EventKind`, stable P identity/reference, Trip/offer/amount source refs as applicable, immutable actor identity, `occurredAt`, `receivedAtUtc` (server), canonical payload version, SHA-256 canonical hash, and source/business correlation. Time fields do not confer dependency or actor eligibility. Exact event-v1 canonical byte serialization, normalized decimals, field order, time representation, hash examples and cross-language fixtures must be written and independently reviewed **before A.2 schema implementation**; reuse APP-01's proven idempotency principles but do not conflate identity namespaces.

Same key + same canonical content: return persisted receipt/state without a second business source or effect. Same key + different content/kind/hash witness: **HTTP 409 CONFLICT**, do not overwrite original record or effects; a retry must **never mint a new ID**. A forged/foreign tenant identity must fail without cross-tenant reads or existence disclosure. Revalidate current rights when an event is about to apply or a CASE is resumed; a revoked user cannot regain a right via retry, stale JWT, or restore.

## 4. Three acknowledgment boundaries and legal lifecycle

1. **Intake:** authenticate/authorize intake, validate canonical identity and shape, durably insert/update receipt within SQL COMMIT; **only then** return the intake ACK (e.g. HTTP 202 and stable receipt/outcome lookup locator). `RECEIVED` does **not** mean money moved. Pre-COMMIT failure has no durable ACK; if response is lost, producer must lookup/retry the **same identity**.
2. **Apply:** the consumer checks the *committed valid P*, matching tenant/Trip/offer, independently witnessed cashier count, non-self actor and current permissions. One explicit transaction atomically posts APP-01D acceptance/source, `CUSTODY-OUT` and `FUND-IN`, source-linked ledger, one-P acceptance uniqueness, a completion marker `APPLIED`, and the associated **transactional outbox message**. A fault rolls back *all* of those apply writes while the earlier committed intake/pending record remains.
3. **Outbound:** dispatcher may deliver at least once, but synthetic receiver ACKs **only after its own durable inbox COMMIT**. This is *not* proof of an external accounting post, invoice, or another financial effect.

Legal persisted states: `RECEIVED` (intake COMMIT) → `PENDING_DEPENDENCY` if required P is missing/undurable; `RECEIVED`/`PENDING_DEPENDENCY` → `APPLIED` only after valid business COMMIT; unrecoverable policy/shape issues → reasoned `REJECTED` when safely attributable; unresolved/expired/retry-exhausted events → owned `CASE`. `CASE` may be resumed under approved Maker/Checker disposition, revalidated and eventually applied or closed with immutable reason. **`UNKNOWN` is strictly a client/observer result** when the response was lost or outcome cannot yet be determined; no automatic UNKNOWN→REJECTED transition. `REJECTED` is nonposting, not a substitute for missing ACK. A syntactically invalid request may fail before durable intake and carries no business effects.

Missing and foreign P must not become a cross-tenant existence oracle: a well-formed but unresolved P reference is indistinguishable externally whether absent or inaccessible; retain C pending then CASE by the approved window. Explicit unauthorized actor operations are denied without disclosing peer-tenant data. All status/lookup routes are tenant and actor scoped.

## 5. Dependency recovery, retry, CASE and retention

- **Event-driven wake-up on committed P** plus an independent **safety scan every 15 minutes**. C-before-P is durably `PENDING_DEPENDENCY`, with **zero acceptance/ledger/outbox effects**, until the correctly identified P arrives.
- **Six** bounded exponential-backoff **technical** retries (e.g. SQL/transient dispatcher faults); do **not** count scheduled checks or time spent waiting for P as technical attempts. Base delay and maximum cap are engineering configuration and must be frozen with deterministic-clock tests before A.3.
- At **24 hours after C receipt** without valid P, move to durable `CASE` with `asOf`, `reason`, `owner=cashier supervision`, `nextAction`, and observable queue/backlog age. Exhausted technical retry budget or irreconcilable dependency triggers an owned CASE as applicable; no silent drop or arbitrary 0-as-unknown report.
- If P arrives **after CASE creation**, it must **not** auto-apply C. Require approved human disposition plus current-role, dependency, count evidence and source/ledger revalidation.
- Preserve original receipt, hash, dependency, status history and audit durably: **no TTL auto-delete**. Production archival, retention duration and RPO/RTO are unresolved APP-02B decisions, not defaults inferred from this synthetic contract.

## 6. Maker/Checker, conflict and immutable correction

Maker = authorized **cashier supervisor**, Checker = a separately authorized person who is **neither Maker nor C initiating actor**. Approval must record immutable maker/checker identities, reason code, evidence link, proposed resolution, disposition, approval time and final source/case linkage. For `CASE` the permitted paths are **HOLD**, **CLOSE with reason**, or **APPROVED_RESUME**; an approval does not itself bypass eligibility checks or post money. Resume **the original EventId** after rechecking P, witness, tenant, source lineage and current authorization; reject stale/superseded or already accepted P.

A conflict (`EventId` same, content different) does not enter an auto-repair path and can never overwrite payload or reassign a new identity. A correction **after APPLIED** is a separate, explicitly authorized, immutable **correction source/command with linkage to the original**, not modification/deletion of the posted `CashHandoverAcceptance` or its ledger legs. Correction mechanics remain nonproduction design/verification and are not authorization for real financial adjustments.

## 7. Transactional Outbox and durable synthetic recipient

On successful C apply, create a `CashHandoverAcceptanceApplied` outbox message **in the same SQL transaction** as source and two ledger effects. Message has stable `MessageId`, tenant, business source, immutable payload/hash/version and delivery/ACK witnesses; there is no actual external invoice/provider/accounting integration.

Dispatcher uses **at-least-once** delivery, retrying with the **same** `MessageId` across ACK loss and restart. The synthetic consumer has a **durable SQL inbox** and validates/deduplicates `MessageId` plus content; ACK only after receiver receipt COMMIT. Duplicate same ID/content returns saved outcome; same ID/different content is conflict without additional consumer effect. A lost consumer ACK must not re-emit the original acceptance or create a second consumer business application. Pending and failed sends remain observable with owner/next action. No exactly-once transport/distributed atomicity claim; source and consumer receipt are separately committed facts.

## 8. Approved A11-01..A11-12 acceptance groups (NOT_RUN)

| ID | Scenario / injected fault | Required independent persisted SQL witness and negative assertion |
|---|---|---|
| **A11-01** | P committed then C; cashier witness valid | OFFER source **nonposting**, exactly one acceptance source and two balanced custody/fund legs; one linked outbox message, consumer receipt after its own COMMIT; no AR change |
| **A11-02** | C intake before P, then correct P arrives | C receipt/P reference durably PENDING; no premature source/ledger/outbox; exactly one eventual acceptance and outbox after P |
| **A11-03** | Missing, invalid or foreign P | Missing/opaque P PENDING→CASE at 24h; invalid local input fail-closed; no foreign P existence oracle, peer-tenant read/write or phantom OFFER |
| **A11-04** | Same event key/content replay | Identical persisted identity/hash/outcome; no new acceptance/outbox or other business writes; one successful P acceptance across multiple C IDs |
| **A11-05** | Same event key with changed payload/kind | HTTP 409; original receipt/outcome/hash unchanged; zero added business/source/ledger effects |
| **A11-06** | Two-worker C/P competition and duplicate intake | Deterministic legal ordering with SQL uniqueness/locks; exactly one acceptance+outbox; no lost pending, duplicate money or masked deadlock |
| **A11-07** | Failure during apply after one or more writes, pre-COMMIT | All acceptance/source/ledger/outbox writes rolled back; separately committed receipt survives; safe same-ID retry posts once |
| **A11-08** | Lost ACK at intake, apply lookup or outbound consumer | Client UNKNOWN resolved via persisted lookup/retry; same EventId/MessageId; no false REJECTED or duplicate effects; durable receiver ACK only after consumer COMMIT |
| **A11-09** | Different OS process / worker restarts while C pending or outbox in flight | Reload SQL pending/leases/delivery state and continue safely; no in-memory-only progress, lost C or duplicated posting |
| **A11-10** | Revoked actor, wrong role, foreign tenant replay/lookup | Denied/revalidated current rights, no resurrection; same-tenant isolation and opposite physical tenant **zero writes/visibility** |
| **A11-11** | Missing P 24h; 6 transient retries; late P; CASE disposition | Clock-driven transition to owned CASE with asOf/reason/next action; Checker ≠ Maker and Checker ≠ C initiator (independently authorized); no auto-apply on late P; approved same-ID resume/HOLD/CLOSE, immutable linked correction and audit as applicable |
| **A11-12** | Full two-tenant source and outbound reconciliation after fault/replay | Independently derived expected/actual/diff; source→offer→acceptance→two legs→outbox→consumer inbox lineage, full-table fingerprints, negative sentinels and cross-tenant no-write; no self-certification via API return |

The **12 groups** are scope coverage IDs, not “12 tests PASS” and not additional immutable business ACs. Each may require many test cases; freeze exact scenario version, decimal expected cells, timestamps, source correlation and mutation sentinels in the A.2–A.4 design handoffs. Independent oracle reads persisted physical SQL; no expected value may be derived from the implementation response. Count/witness/role negatives, CASE/correction/fault paths and outbound ACK must be explicitly mapped to the groups; do not hide them as optional follow-ups.

## 9. Architecture sketch and PR handoff (no implementation in A.1)

A.2 schema proposal: event inbox receipt unique on `(TenantId,ProducerId,EventId)` **without hash in the unique key**; stable C→P dependency reference scoped to server-bound tenant; immutable event canonical hash/version and actor/source history; unique per-P acceptance protection; source-linked transactional outbox with recipient delivery/ACK states; durable synthetic consumer inbox; case/approval/correction journal. Enforce tenant FK/unique constraints and append-only/transition invariants. Integrate with APP-01D source/command/ledger; do not add a parallel accounting truth. Exact table names/DDL/HTTP routes are **not** authorized by this documentation alone.

- **A.1 (this docs PR):** record Owner choices, traceability, states, scope and 12-group oracle handoff; independent review and checked final HEAD still needed before `CONTRACT_APPROVED`.
- **A.2 (separate PR after A.1 accepted):** migrations/schema; fresh/replayed migrations in **two physically separate nonce-owned LocalDB tenant databases**, unique/FK/immutable/cross-tenant negatives; **no posting behavior**.
- **A.3 (separate PR after A.2):** explicit transactional inbox processing, P-triggered/safety recovery, CASE Maker/Checker, APP-01D bridge, source/ledger posting, outbox/consumer and fault/restart/two-worker checks; **no production consumer**.
- **A.4 (separate PR after A.3):** independently sourced SQL expected/actual/diff for all A11-01..12 groups on both tenants; no missing cells; complete source/effect/inbox/outbox/case lineage, peer-tenant no writes, rollback/replay/lost-ACK/OS restart; TestRunId, exact reviewed PR HEAD and green CI, with previous APP-01E.5 **historical** 247/247 tests / AC-03 / frontend/PWA/Playwright regressions preserved. Do not claim these historical counts for this unrun increment or pre-set its new test floor.

## 10. A.1 exit gate vs production/verification boundaries

**A.1 may become `CONTRACT_APPROVED` only after:** Owner record is linked; independent review of this contract and the 12-group expected/actual SQL witness plan has no unresolved blockers; exact reviewed HEAD and required CI are verified; the concrete C/P identity/meaning, actor/tenant boundary, current-permission checks, intake/apply/outbound ACK and legal states, retry/case/maker-checker/retention, source-bridge and exclusions are reconciled; immutable business lock and prior tests are preserved. Until then: **OWNER_DECISIONS_APPROVED / DOCUMENTATION_REVIEW_PENDING**, **A.2 BLOCKED_ON_A1_REVIEW**, no AC-11 pass claim.

**Only A.4 passing all gates can be labeled `AC11_SYNTHETIC_VERIFIED`**. EV-08 actual-environment evidence is **NOT_VERIFIED**, and G-IDEMP plus G-STOCK/G-CUT/G-LEDGER/G-INV production P0 gates remain **NOT_VERIFIED**; APP-02B–E, production actor/tenant auth, real cash/invoice/accounting, offline writes, B_NO_CHANNEL, pilot and tenant go-live remain separate forbidden/ungated scopes. The Owner's 125% browser display exception remains `EXCEPTION_ACCEPTED / NOT_TESTED`, not a new acceptance requirement.

**References:** [Owner APP-02 decisions](../decisions/owner-app02-2026-11-17.md) · [APP-02 roadmap](app-02.md) · [APP-00–APP-01 historical closeout](app-00-app-01.md) · [AC-11](../../business/acceptance.md) · [BR-16](../../business/business-rules.md) · [POL-16](../../business/policies.md).
