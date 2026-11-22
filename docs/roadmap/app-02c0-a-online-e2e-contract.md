# APP-02C.0 — A_ONLINE E2E Business, API/UI & Independent Oracle Contract

> **BUSINESS CONTRACT RETAINED; DEV TIMING AMENDED 2026-11-22.** The [10/10 original decisions](../decisions/owner-app02c-2026-11-19.md) and [merged PR #77](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77) establish C.0 `CONTRACT_APPROVED (DOCS_ONLY)`. [OWN-APP02C-DEV-01..06](../decisions/owner-app02c-dev-first-2026-11-22.md) authorizes C.1–C.5 source, owned DEV migrations, UI and actual integrated execution with synthetic data before physical B completion. The [C roadmap](app-02c-a-online-e2e.md) tracks implementation and verification. PREPROD/C.6 and production retain their own gates.

[APP-02 staged roadmap](app-02.md) · [APP-02C implementation roadmap](app-02c-a-online-e2e.md) · [DEV handoff](app-02c-dev-handoff.md) · [Operations debt](app-02c-operations-debt.md) · [Business AC](../../business/acceptance.md) · [Business BR](../../business/business-rules.md) · [Business POL](../../business/policies.md).

## 1. Purpose and bounded coverage

Implement one vertical A_ONLINE flow incrementally in the **authorized synthetic DEV scope**; physical operational execution has separate admission:

1. Customer, optional Outlet and registered/ad-hoc validated ShipTo.
2. ConfirmAOrder: approved commercial policy and immutable snapshots.
3. ReserveAStock: independent SQL command and allocation.
4. LoadManifest: MAIN → TRIP without a sale effect.
5. PostDelivery: each actual partial delivery has source-bound stock/COGS/AR.
6. Per-Delivery due-dated AR, authoritative SQL report and UI.

**Within proposed C business acceptance:** AC-02, AC-08 and **A-related assertions only** of AC-03, plus relevant BR/POL security, event/recovery and report invariants. AC-03's B sale, B quota, cash, custody/fund and full mixed-trip final totals are outside C; C does not earn a complete AC-03 or full product pass.

**Outside scope:** B_ONLINE/Mixed workflow, live invoice provider, external accounting reconciliation, offline writes, physical warehouse cutover, legacy writer authority, Pilot/production admission, full EV-01..15 and production P0 tests. APP-02A synthetic C→P evidence is regression/reference, not C E2E proof. The Business Baseline `business/` and `business.lock.json` remain immutable.

## 2. Source of authority and cross-cutting invariants

| Concern | Mandatory invariant | Baseline / Owner |
|---|---|---|
| Tenant and actor | SQL/control-plane authoritative tenant binding, active membership, permissions and current revocation epoch checked at command COMMIT; deny wrong DB, stale token, queue retry or restoration of revoked authority. | BR-01/17, APP-02B.3–B.5, OWN-APP02C-03/09 |
| Debtor and delivery address | Customer is debtor independent of Outlet; Order/Delivery carries immutable ShipTo snapshot. One-off ShipTo is permitted only after server validation/provenance. | BR-02, AC-02, OWN-APP02C-02 |
| Commercial policy | Resolve applicable tenant/seller/Customer-scope approved commercial policy at Confirm and freeze its source/version/effective-time/precedence; required `UNSET` fails closed. Later delivery verifies that immutable snapshot's approval at Confirm, separately from current execution authority; expiry/supersession alone does not replace frozen terms. | BR-08/10, POL-08/10, OWN-APP02C-03 |
| Confirm and Reserve | Separate SQL commits and independently idempotent command IDs. Confirm freezes terms but no AR, revenue or COGS; only Reserve actually holds stock. Confirmed-unreserved cannot load. | BR-03/04, OWN-APP02C-04; **review semantics C0-R01** |
| Load | Single atomic source and stock/value ledger transition MAIN→TRIP tied to valid reservation. Load has no AR/revenue/COGS. | BR-04/07, OWN-APP02C-05 |
| Delivery | Each real delivery creates one committed effect of source-bound TRIP stock, COGS and Customer AR, never more than available/held; `HELD` remainder is not automatically sold/released. | BR-03/07, OWN-APP02C-06 |
| Due date | Due for each Delivery calculated from seller-local **business date** and Confirm terms N calendar days, not device timestamp or N×24h. Historical AR due dates do not change when Customer master changes. | BR-10, AC-08, OWN-APP02C-07 |
| Reporting | SQL source/AR state is authoritative; show `asOf`, pending/reconciliation, Customer/Delivery/balance, overdue and source; no fabricated zero from missing/out-of-sync inputs. | BR-23, OWN-APP02C-07/08 |
| Durable identity | Same source ID + same canonical payload yields stable persisted outcome; same ID + different payload conflict; failures before COMMIT rollback; after COMMIT/lost ACK lookup original outcome; never mint a replacement ID implicitly. | BR-16, OWN-APP02C-04..09 |
| Development and physical verification | DEV uses actual scoped API/SQL/UI, reviewed expected/actual differences and tenant negatives. C.6 separately needs physical identities, witnesses and custody under its admission contract. DEV/CI results never promote themselves to PREPROD. | APP-02B B.1/B.9, OWN-APP02C-09/10; OWN-APP02C-DEV-01..06 |

### 2.1 Existing APP-01 behavior versus the approved C target

Reuse means preserving transaction, source-identity and accounting invariants. It does **not** mean the existing APIs already implement the selected C flow. The following compatibility changes are work inside the named authorized increments, before their dependent behavior is accepted. They do not block independent C.1 work. This documentation amendment changes no command, counter, schema or historical evidence.

| Area | Existing implementation at the PR base | Required C compatibility decision and evidence |
|---|---|---|
| Confirm / Reserve order | [APP-01A.2](app-01a2-command-transactions.md) and [command service](../../src/FmcgDms.Api/Persistence/App01a2CommandService.cs) reserve only on DRAFT, then Confirm only after full reservation. [Existing tests](../../tests/FmcgDms.Api.Tests/App01a2CommandTests.cs) verify that ordering. | C.2 must map the Owner's Confirm → Reserve target to explicit states/preconditions under **C0-R01**. Cover `CONFIRMED_UNRESERVED`, partial/full reserve, Reserve failure/retry without reconfirmation, and no Load without sufficient reserve. Review downstream full-reservation guards and compatibility of historical source IDs/outcomes; preserve existing synthetic regression coverage without relabeling old evidence. |
| Load versus delivery entitlement | [APP-01B.2](app-01b2-manifest-commands.md) leaves StockRight and StockPool unchanged at Load. [APP-01C.3 posting](../../src/FmcgDms.Api/Persistence/App01c3APostingService.cs) requires actual delivered total to equal `StockRight.ConsumedBaseQuantity`, then consumes the right on Delivery. | Under **C0-R04**, distinguish warehouse reservation occupancy, loaded custody quantity and retained Order delivery entitlement. Load must not debit the existing delivery-consumed counter and then debit it again at Delivery. Freeze source-linked conservation equations and the meaning of each transition before C.3/C.4 implementation. Require reserve40 → load40 → deliver25 → held15, partial load with visible warehouse remainder, competing/replayed loads, one delivery consumption, authorized release/unload, rollback and recovery evidence. |
| Partial-load reuse | Existing Load posts every staged line's full planned quantity. The [physical schema](../../src/FmcgDms.Api/Persistence/App01b1PhysicalStock.cs) permits one manifest per Trip and one manifest line per StockRight; an already loaded manifest is not an append/reload API. | **C0-R04** must specify how an explicitly partial reserved quantity is staged and how its remainder is later loaded, retained or released. Do not assume repeated partial loads on the same right are already supported, or weaken uniqueness without reviewed source/quantity and concurrency protections. |
| Policy validity after Confirm | [B.6 `b6_ResolveCurrent`](app-02b6-policy-registry-approvals.md) checks current SQL-UTC validity for a new policy decision. APP-01C.3 validates original approved terms against the Confirm snapshot. | C.2 resolves applicable policy when confirming; C.4 proves frozen commercial approval at Confirm. Do not run a current-only resolver against historical terms at every delivery. Current actor/tenant/writer revocation and any explicitly approved delivery-time restriction still apply. **C0-R03** must define these separate checks and reject forged/unapproved snapshots. |

## 3. Logical data contract (not a database schema migration)

The following fields describe **evidence and invariants**, not mandatory table or API names. Later increments must map them to existing APP-01 entities/commands and approved APP-02B authority without duplicating ledgers.

| Logical object | Minimum authoritative lineage / fields |
|---|---|
| `Customer` | Tenant binding, CustomerId, debtor state/version; 0..n Outlet references. No implicit Outlet requirement. |
| `ShipToSnapshot` | CustomerId, optional OutletId, registered ShipToId **or** validated direct ShipTo canonical payload, address provenance, immutable normalized snapshot bytes/hash, effective source/actor and version. |
| `AOrderConfirm` | TenantId, seller/actor session+permission epoch, OrderId/version, command identity and canonical hash, frozen ShipTo, price/discount/tax/UoM/terms/credit-policy source/version/effective date, recorded UTC and local business date. |
| `AReservation` | Separate command identity, confirmed OrderId/version, SKU/base UoM, warehouse/location, quantity held/remaining, non-overlap with B quota and other reservation, constraint and ledger source. |
| `LoadManifest` | TripId/Trip writer, manifest identity/hash, exact reservation source/quantity, MAIN/TRIP stock and value ledger references, after-commit outcome; optional explicitly partial load. |
| `ADelivery` | DeliveryId and command identity/hash, OrderId/Trip/Reservation/loaded-source, actual delivered business date, ShipTo snapshot/source reference, SKU/qty, source-bound stock/COGS legs, AR source and approved terms version. |
| `ARSource` | Customer debtor, DeliveryId, amount/currency, due date, source identity, reconciliation/allocations/corrections and `asOf`; never copy-recalculate from current master. |
| `CommandOutcome` | Immutable command identity/payload hash, persisted status, source/fault/CASE lineage, safe recovery lookup, current authoritativeness, UTC timestamps and operator next action. |

**Snapshot correction:** an authorized amendment produces a new immutable source/version for eligible future scope, never overwrites historical Order or Delivery payload. Exact amendment state and fields require separate contract review (C0-R02).

## 4. API command protocol (C.1–C.5 DEV implementation authorized)

Each eventual command must be tenant-bound by server authority (not a trusted client-supplied database), have a unique stable command identity, canonical payload/hash, expected source version, current actor/tenant/writer permission check at commit, durable outcome and correlation IDs. Commercial policy selection and validity for frozen price/terms are anchored to Confirm; subsequent commands validate the immutable approved snapshot and separately enforce any explicitly approved current execution restrictions. Expiry or supersession of commercial policy after Confirm alone neither invalidates that snapshot nor permits re-pricing/re-terms. Concrete URL, HTTP status codes, DB stored procedures and DTO schema are **to be finalized per authorized increment**, not invented in C.0.

| Operation / conceptual state | Preconditions | Atomic source/effect | Fail-closed / recovery |
|---|---|---|---|
| `ConfirmAOrder` (DRAFT → CONFIRMED / may be UNRESERVED) | Current actor rights, valid Customer/ShipTo, approved price/terms/credit and order version | Commit frozen order and policy/ShipTo snapshots; **zero AR, sales and COGS** | Policy `UNSET`, invalid ShipTo or stale actor denied; same-ID retry returns recorded outcome; later Reserve can be attempted separately |
| `ReserveAStock` (UNRESERVED → RESERVED partially/fully) | Confirmed order, valid remaining quantity and current permission; reservation and B quota exclusivity | One serializable/otherwise proved atomic SQL source+stock-hold effect | Insufficient stock leaves truthful unreserved/partial outcome; cannot Load beyond valid reserve; no AR/COGS |
| `LoadManifest` (RESERVED → LOADED for explicit units) | Valid reservation, Trip/writer/actor, base quantity and stock source | Atomic MAIN−q, TRIP+q, stock value and reservation-to-load source binding, ledger/source and outcome; reservation/loaded remainder mapping must satisfy C0-R04 and must not count goods as delivered | One bad manifest precondition rejects/rolls back entire requested manifest; same-ID retry cannot double-load; loaded units cannot be reused by a competing load |
| `PostDelivery` (LOADED → DELIVERED for explicit units) | Actual delivery evidence, loaded A stock, valid Customer/ShipTo, approved immutable Confirm snapshot, quantities not previously delivered | Atomic Delivery source + TRIP−q + one delivery-entitlement consumption + COGS + AR due-dated from Confirm terms | Overdelivery, changed payload, wrong tenant, forged/unapproved snapshot, stale actor or an explicitly approved current execution restriction denies posting; commercial-policy expiry/supersession alone does not. Physically occurred delivery with failed write is retained as occurred-event/CASE, not silently discarded |
| `GetCommandOutcome` / `GetDeliverySource` | Current authorized session/tenant/resource; supplied same immutable source identity | **Read only** authoritative persisted result | UNKNOWN remains UNKNOWN/PENDING, not converted to REJECTED; do not create new command identity after lost ACK |
| `GetARReport(asOf, filter)` | Current Customer/reporting permission, tenant-bound authoritative SQL | **Read only** sources, allocation/correction/reconciliation and due date | Show incomplete/pending, `asOf` and denominator; no cross-tenant data leakage or zero-as-unknown |

**UX rule:** auto-refresh and navigation may read, but must **never auto-issue the next business command**. A button action is a single explicit server-authorized command. Draft-only local UI edits are not offline business writes.

## 5. Provisional independent acceptance scenarios (SPECIFIED_DRAFT / NOT_RUN)

The following are **proposed scenario definitions**, not an approved complete matrix, fixed test count, result or expected fixture. Independent expected fixtures must be frozen and approved **before** execution; case IDs/expected amounts may be adjusted by review without modifying canonical `business/`.

| Proposed Case | Positive invariant | Negative/fault evidence | Primary AC/BR |
|---|---|---|---|
| C-A02-01 | Customer C1 without Outlet can Confirm and deliver to validated direct ShipTo S1; AR belongs to C1 | Invalid direct address/mismatched Customer or cross-tenant ShipTo denied | AC-02, BR-02 |
| C-A02-02 | Frozen Confirm ShipTo survives subsequent Customer/Outlet/ShipTo master modification | No retroactive update of already Confirmed/Delivered snapshots | AC-02, BR-08 |
| C-A02-03 | Approved source/version amendment for eligible undelivered part creates new immutable version | Stale, unsigned or already-delivered amendment rejected | AC-02, POL-08 |
| C-A03A-01 | Confirm freezes policy with AR/COGS both zero; separate valid Reserve holds authorized A stock | `UNSET`, expired, wrong-scope policy; Confirm but Reserve failure cannot Load | AC-03(A), BR-03 |
| C-A03A-02 | Reserved stock excludes B quota and competing A allocations; concurrent Reserve is linearized | Double reservation, invalid tenant/worker or over-reserve denied | AC-03(A), BR-04 |
| C-A03A-03 | Partial LoadManifest atomically moves reserved A stock MAIN→TRIP without AR/COGS | Mid-transaction fault rollback, stale manifest/replay cannot double-load | AC-03(A), BR-04 |
| C-A03A-04 | Delivery of 25 from A40 posts only its stock/COGS/AR; 15 remains held per source | Overdelivery, other Trip, replay/changed payload and wrong physical DB denied | AC-03(A), BR-03/07 |
| C-A03A-05 | Independent SQL stock/value and AR reconciliation after each commit | Mutation of fixture/source/hash, peer tenant write/read, lost ACK and process restart fail or safely recover | AC-03(A), BR-16/17 |
| C-A08-01 | Confirm N=7, Delivery local dates 01/11 and 08/11 yield dues 08/11 and 15/11 | N not from snapshot, timezone/device-time arithmetic rejected | AC-08, BR-10 |
| C-A08-02 | Change Customer master N to 14, or expire/supersede the original terms policy between deliveries: the Confirmed Order retains N=7 and its per-Delivery dues; a new Order Confirmed with approved N=14 and delivered 08/11 is due 22/11 | Retroactive master/policy rebind and forged/unapproved/wrong-scope snapshot rejected; absent/UNSET or expired policy blocks a new Confirm | AC-08, BR-08/10 |
| C-A08-03 | `asOf` report reconciles Customer/Delivery, allocations/corrections, due dates, overdue/pending | Wrong tenant/report filter, forged page subtotal, unknown=0 rejected | AC-08, BR-23 |
| C-SEC-01 | Cross-tenant current actor/permission/epoch denial applies to UI, API, Worker and SQL target | Restore stale revoke, wrong tenant physical DB, System Admin implicit access denied | BR-01/17, B G-B01 |
| C-REC-01 | Before-COMMIT rollback; after-COMMIT stable outcome and exactly-once business effect on replay | Lost ACK, same-ID changed payload, restart, two-client/two-worker races | BR-16, APP-01E |
| C-UI-01 | Workspace shows actual outcomes, snapshots, `asOf` and next action, including intermediate unreserved/HELD/CASE | False success from HTTP alone, auto-next-command, offline business write rejected | OWN-APP02C-08 |

### Worked business oracle references — not live policy values

- **AC-02:** C1 has no Outlet but has valid S1; after A Confirm and Delivery, **C1 remains debtor** and ShipTo/price/terms snapshots are invariant under master changes.
- **AC-08:** A1 Confirm with **N=7** → Delivery 01/11 due **08/11** and Delivery 08/11 due **15/11**. Master later changes N to **14**; A1's due dates are unchanged. A2 newly Confirmed with N=14 and delivered 08/11 is due **22/11**. Year and seller timezone are deliberately not fabricated from this baseline scenario.
- **AC-03:** canonical combined example `MAIN100 @ cost1.000`, A40/B30, selling price 15 and B grant 100. Its full outcome includes B and cash/custody, **not C scope**. For the A slice, A delivery of 25 produces **AR_A375** with A remainder **held15**; expected A COGS/value and stock movements must be independently derived/frozen from approved source cost/manifest and validated, not misreport full-scenario COGS450 as A-only.

## 6. UI & report design contract

Existing frontend is reused; **no new standalone app**. Desktop-first modules: Customer/ShipTo, A Orders, Reservations, Trip/Manifest, Delivery, AR Report. A selected Order detail presents Confirm policy/ShipTo snapshot, reservation state, loaded quantity, per-Delivery source, held remainder, due dates and command outcomes. Each C.1–C.4 increment includes the minimum real UI for its API/SQL behavior; C.5 completes workspace/report integration. Reuse adopted UI-01–03 controls and [UI/UX guidance](../ui-ux/README.md). Prior UI-04 readiness PR #86 is not a C.1 implementation dependency.

- Explicit Confirm/Reserve/Load/Delivery commands, disabled/hidden according to current **server** authority, always rechecked server-side at commit.
- Read-only status can refresh; detect stale resource version. Keep stable command identity across timeout/lost ACK; offer outcome lookup and CASE/operator workflow rather than sending a new business command.
- Avoid optimistic success beyond committed server outcome; visually distinguish pending, conflict, fail-closed, held remainder, posted and unknown/requires reconciliation. Concrete status vocabulary/HTTP responses are a later increment contract.
- AR filtering by Customer, Delivery, due date, overdue, `asOf`; show pending/source coverage and export tenant-scoped with permission checks. Report query must reconcile with authoritative AR source, not a client-computed aging cache.
- No offline business write queue, no B_NO_CHANNEL activation and no real invoice/accounting dispatch.

## 7. Independent evidence and gate model

| Gate | Evidence needed before promotion | Current state |
|---|---|---|
| `C-PLAN` | C.0 10/10 decisions, bounded contract + reviewed scenario proposal and explicit future blockers, zero documentation findings, exact source-head required CI and merged docs-only PR | Disposition in [PR #77 review/CI/merge record](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77) |
| `C-DEV` | [2026-11-22 standing Owner authorization](../decisions/owner-app02c-dev-first-2026-11-22.md) for C.1–C.5 source/migrations/UI and synthetic integrated DEV execution. Applicable contract details are resolved within the increment. | `OWNER_APPROVED / DEV_AUTHORIZED` |
| Per-increment DEV verification | Actual scoped UI→API→SQL execution, reviewed expected values and negatives, source/environment lineage and relevant regression results. Initial target preserves nonce-owned LocalDB controls. | [C.1 post-merge DEV closeout](../closeout/2026-11-22-app-02c1-dev.md), [C.2 post-merge DEV closeout](../closeout/2026-11-22-app-02c2-dev.md). [C.3](../closeout/2026-11-22-app-02c3-dev.md) and [C.4](../closeout/2026-11-22-app-02c4-dev.md) are verified in synthetic DEV and CI. See the [C.5 report contract](app-02c5-workspace-reporting.md) and C roadmap for its DEV status. |
| `C-PREPROD` | APP-02B `PREPROD_FOUNDATION_VERIFIED` including all **30 B.1** cases, 5 gates, 2 RUNTIME probes, #53/restore and independent reviewer requirements; approved real A commercial policy and separate runtime test authority | `BLOCKED` |
| `C-E2E` | Owner-approved final C scenario/fixture matrix; UI→API→SQL two physical PREPROD tenant test; independent expected/actual/diff and peer read/write negatives; fault/restart, TestRunId/source HEAD/image/DB digests, CI/regression, reviewer evidence | `NOT_RUN` |

At a future C-E2E execution record for **each approved case × applicable tenant/scope**: `CaseId`, `TestRunId`, source HEAD, build/image digest, environment ID, physical TenantDatabaseId/binding version, actor/session/permission epoch, policy/ShipTo/Order/Delivery source IDs and hashes, UTC and seller business date, fault ID, independently owned frozen expected with fixture SHA, SQL independently observed actual, signed difference, source/ledger/AR SQL before-after fingerprints, peer read/write probe, required UI/HTTP/OTel artifact links and SHA-256, reviewer identity/disposition, finding IDs. Redact PII/secrets. A response body alone does not certify a case.

**Reviewer separation:** physical C.6/B.9 retains its externally approved observer/Security/DBA trust requirements unless explicitly replaced. These are no longer prerequisites for DEV work. Per-feature independent code/SQL/expected-result review remains necessary; describe automated/agent-assisted review truthfully, without claiming multiple human physical witnesses. Earlier APP-02B.4/#59 risk exceptions are not inherited or extended.

**Regression retention:** Existing APP-01E (cross-transaction SQL), APP-02A.4 (A11 synthetic), APP-02B bounded checks, full APP-00A, Node full+production audit, frontend/PWA/Playwright and new C suites must be tracked at the **same final reviewed source HEAD** when applicable. Required failures/skips cannot be silently waived. Historical one-off reruns do not close Issue #75.

## 8. Contract review dispositions and blocking gates

The original six `DEFERRED / BLOCKING` rows are now **scoped by the 2026-11-22 Owner decision**. None is silently closed. C.1–C.5 may start their contract and independent implementation work; resolve a row before implementing/accepting the behavior that relies on it. A material unresolved business interpretation pauses that dependent subtask, not the entire project. Routine implementation choices consistent with the baseline can be documented by the developer and reviewed without repeating a blanket Owner interview.

| Item / current disposition | Owning increment or physical gate | Required contract/evidence |
|---|---|---|
| **C0-R01 — C.2 DEV CONTRACT RECORDED AND VERIFIED** | C.2 state/preconditions; dependent C.3/C.4 | [C.2 contract](app-02c2-confirm-reserve.md) records BR-03 interpretation, immutable draft and outcomes, Confirm→Reserve states, and legacy Load exclusion. Historical APP-01 command ordering retains its own verification. |
| **C0-R02 — ADDRESS, CONFIRM BINDING AND DELIVERY AMENDMENT COVERED IN DEV** | C.1 address and selection, C.2 Confirm binding, C.4 amendment enforcement | [C.1](app-02c1-customer-shipto.md) validates address revisions, [C.2](app-02c2-confirm-reserve.md) freezes the Confirm ShipTo, and [C.4](app-02c4-partial-delivery-ar.md) adds independently approved amendments for undelivered scope with version and unresolved-evidence checks. |
| **C0-R03 — CONFIRM AND DELIVERY PORTIONS COVERED IN DEV** | Confirm policy in C.2, Delivery in C.4 and reporting in C.5 | [C.2](app-02c2-confirm-reserve.md) freezes approved policy and its source hash. [C.4](app-02c4-partial-delivery-ar.md) verifies approval at Confirm and derives AR due dates from the seller-local Delivery date and frozen terms. [C.5](app-02c5-workspace-reporting.md) consumes frozen due dates and seller business days in the bounded synthetic report. |
| **C0-R04 — RESERVE, LOAD AND DELIVERY PORTIONS COVERED IN DEV** | C.2 warehouse occupancy, C.3 custody, C.4 consumption and held remainder | [C.2](app-02c2-confirm-reserve.md) uses the shared allocation pool, [C.3](app-02c3-atomic-load-manifest.md) transfers custody while preserving delivery entitlement, and [C.4](app-02c4-partial-delivery-ar.md) independently reconciles one consumption, 15 EA held and AR/COGS. Authorized release and unload retain their own source and evidence requirements. |
| **C0-R05 — DEFERRED_TO_PHYSICAL_ACCEPTANCE** | B.9 physical claim and C-PREPROD/C.6 only | [OPS-C05](app-02c-operations-debt.md) tracks actual reviewer trust, identities/keys and custody, or a separately approved replacement. No false multi-person attestation. This row does not block C.1–C.5 DEV. |
| **C0-R06 — REQUIRED_PER_FEATURE / PHYSICAL_PART_DEFERRED** | Each increment's acceptance; C.6 separately | Developer and independent fixture/SQL reviewer version and check expected results and negatives before acceptance execution; raise material business-value ambiguity with the Owner. Freeze fixture hashes and report actual case counts/results. Physical B/C witness/custody/matrix obligations remain in [OPS-C05/06](app-02c-operations-debt.md). The provisional scenario list is not a PASS count. |

**Current authorization and result.** [PR #77](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77) records C.0 contract approval. The dated Owner decision authorizes C DEV and the [C roadmap](app-02c-a-online-e2e.md) tracks each increment. `C_PREPROD_NOT_VERIFIED / C_E2E_NOT_RUN`.
