# APP-02C.0 — A_ONLINE E2E Business, API/UI & Independent Oracle Contract

> **OWNER-APPROVED DESIGN PROPOSAL — DOCS-ONLY PR REVIEW PENDING.** Contract-only proposal, dated 2026-11-19, from [10/10 Owner decisions](../decisions/owner-app02c-2026-11-19.md). `APP-02C.0_DOCS_ONLY` is approved to prepare this design; this document is **not yet `CONTRACT_APPROVED`**. Source, SQL migrations, business transaction routes and PREPROD runtime are **not authorized**.

[APP-02 staged roadmap](app-02.md) · [APP-02C proposed implementation roadmap](app-02c-a-online-e2e.md) · [Business AC](../../business/acceptance.md) · [Business BR](../../business/business-rules.md) · [Business POL](../../business/policies.md).

## 1. Purpose and bounded coverage

Design one vertical A_ONLINE flow for a **future separately authorized** incremental implementation:

```text
Customer (+ optional Outlet) / registered-or-ad-hoc ShipTo
   -> ConfirmAOrder [approved commercial policy + snapshots]
   -> ReserveAStock [independent SQL command and allocation]
   -> LoadManifest [MAIN -> TRIP / no sale effect]
   -> PostDelivery [each actual partial delivery, source-bound stock/COGS/AR]
   -> Due-dated AR [per-delivery terms snapshot] -> SQL report / UI
```

**Within proposed C business acceptance:** AC-02, AC-08 and **A-related assertions only** of AC-03, plus relevant BR/POL security, event/recovery and report invariants. AC-03's B sale, B quota, cash, custody/fund and full mixed-trip final totals are outside C; C does not earn a complete AC-03 or full product pass.

**Outside scope:** B_ONLINE/Mixed workflow, live invoice provider, external accounting reconciliation, offline writes, physical warehouse cutover, legacy writer authority, Pilot/production admission, full EV-01..15 and production P0 tests. APP-02A synthetic C→P evidence is regression/reference, not C E2E proof. The Business Baseline `business/` and `business.lock.json` remain immutable.

## 2. Source of authority and cross-cutting invariants

| Concern | Mandatory invariant | Baseline / Owner |
|---|---|---|
| Tenant and actor | SQL/control-plane authoritative tenant binding, active membership, permissions and current revocation epoch checked at command COMMIT; deny wrong DB, stale token, queue retry or restoration of revoked authority. | BR-01/17, APP-02B.3–B.5, OWN-APP02C-03/09 |
| Debtor and delivery address | Customer is debtor independent of Outlet; Order/Delivery carries immutable ShipTo snapshot. One-off ShipTo is permitted only after server validation/provenance. | BR-02, AC-02, OWN-APP02C-02 |
| Commercial policy | Tenant/seller/Customer-scope B.6 approved policy, version/effective-time/precedence; all required prices, discounts, tax profiles, terms and credit checks must be approved; `UNSET` must fail closed. | BR-08/10, POL-08/10, OWN-APP02C-03 |
| Confirm and Reserve | Separate SQL commits and independently idempotent command IDs. Confirm freezes terms but no AR, revenue or COGS; only Reserve actually holds stock. Confirmed-unreserved cannot load. | BR-03/04, OWN-APP02C-04; **review semantics C0-R01** |
| Load | Single atomic source and stock/value ledger transition MAIN→TRIP tied to valid reservation. Load has no AR/revenue/COGS. | BR-04/07, OWN-APP02C-05 |
| Delivery | Each real delivery creates one committed effect of source-bound TRIP stock, COGS and Customer AR, never more than available/held; `HELD` remainder is not automatically sold/released. | BR-03/07, OWN-APP02C-06 |
| Due date | Due for each Delivery calculated from seller-local **business date** and Confirm terms N calendar days, not device timestamp or N×24h. Historical AR due dates do not change when Customer master changes. | BR-10, AC-08, OWN-APP02C-07 |
| Reporting | SQL source/AR state is authoritative; show `asOf`, pending/reconciliation, Customer/Delivery/balance, overdue and source; no fabricated zero from missing/out-of-sync inputs. | BR-23, OWN-APP02C-07/08 |
| Durable identity | Same source ID + same canonical payload yields stable persisted outcome; same ID + different payload conflict; failures before COMMIT rollback; after COMMIT/lost ACK lookup original outcome; never mint a replacement ID implicitly. | BR-16, OWN-APP02C-04..09 |
| Physical verification | Independent expected/actual/difference with original SQL state, two physical tenant DB identities and peer no-read/no-write witness. No source-only/CI promotion to PREPROD. | APP-02B B.1/B.9, OWN-APP02C-09/10 |

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

## 4. Proposed API command protocol (no new routes authorized)

Each eventual command must be tenant-bound by server authority (not a trusted client-supplied database), have a unique stable command identity, canonical payload/hash, expected source version, current permission/policy check at commit, durable outcome and correlation IDs. Concrete URL, HTTP status codes, DB stored procedures and DTO schema are **to be finalized per authorized increment**, not invented in C.0.

| Operation / conceptual state | Preconditions | Atomic source/effect | Fail-closed / recovery |
|---|---|---|---|
| `ConfirmAOrder` (DRAFT → CONFIRMED / may be UNRESERVED) | Current actor rights, valid Customer/ShipTo, approved price/terms/credit and order version | Commit frozen order and policy/ShipTo snapshots; **zero AR, sales and COGS** | Policy `UNSET`, invalid ShipTo or stale actor denied; same-ID retry returns recorded outcome; later Reserve can be attempted separately |
| `ReserveAStock` (UNRESERVED → RESERVED partially/fully) | Confirmed order, valid remaining quantity and current permission; reservation and B quota exclusivity | One serializable/otherwise proved atomic SQL source+stock-hold effect | Insufficient stock leaves truthful unreserved/partial outcome; cannot Load beyond valid reserve; no AR/COGS |
| `LoadManifest` (RESERVED → LOADED for explicit units) | Valid reservation, Trip/writer/actor, base quantity and stock source | Atomic MAIN−q, TRIP+q, stock value and reservation consumption, ledger/source and outcome | One bad manifest precondition rejects/rolls back entire requested manifest; same-ID retry cannot double-load |
| `PostDelivery` (LOADED → DELIVERED for explicit units) | Actual delivery evidence, loaded A stock, valid Customer/ShipTo, quantities not previously delivered | Atomic Delivery source + TRIP−q + COGS + AR due-dated from Confirm terms | Overdelivery, changed payload, wrong-tenant, stale policy/actor or duplicate effect denied; physically occurred delivery with failed write retained as occurred-event/CASE, not silently discarded |
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
| C-A08-02 | Change Customer master N to 14 after Confirm: prior dues unchanged; new Order delivered 08/11 due 22/11 | Retroactive master recalc rejected; absent/UNSET N blocks Confirm | AC-08, BR-08/10 |
| C-A08-03 | `asOf` report reconciles Customer/Delivery, allocations/corrections, due dates, overdue/pending | Wrong tenant/report filter, forged page subtotal, unknown=0 rejected | AC-08, BR-23 |
| C-SEC-01 | Cross-tenant current actor/permission/epoch denial applies to UI, API, Worker and SQL target | Restore stale revoke, wrong tenant physical DB, System Admin implicit access denied | BR-01/17, B G-B01 |
| C-REC-01 | Before-COMMIT rollback; after-COMMIT stable outcome and exactly-once business effect on replay | Lost ACK, same-ID changed payload, restart, two-client/two-worker races | BR-16, APP-01E |
| C-UI-01 | Workspace shows actual outcomes, snapshots, `asOf` and next action, including intermediate unreserved/HELD/CASE | False success from HTTP alone, auto-next-command, offline business write rejected | OWN-APP02C-08 |

### Worked business oracle references — not live policy values

- **AC-02:** C1 has no Outlet but has valid S1; after A Confirm and Delivery, **C1 remains debtor** and ShipTo/price/terms snapshots are invariant under master changes.
- **AC-08:** A1 Confirm with **N=7** → Delivery 01/11 due **08/11** and Delivery 08/11 due **15/11**. Master later changes N to **14**; A1's due dates are unchanged. A2 newly Confirmed with N=14 and delivered 08/11 is due **22/11**. Year and seller timezone are deliberately not fabricated from this baseline scenario.
- **AC-03:** canonical combined example `MAIN100 @ cost1.000`, A40/B30, selling price 15 and B grant 100. Its full outcome includes B and cash/custody, **not C scope**. For the A slice, A delivery of 25 produces **AR_A375** with A remainder **held15**; expected A COGS/value and stock movements must be independently derived/frozen from approved source cost/manifest and validated, not misreport full-scenario COGS450 as A-only.

## 6. UI & report design contract

Existing frontend is reused; **no new standalone app**. Desktop-first modules: Customer/ShipTo, A Orders, Reservations, Trip/Manifest, Delivery, AR Report. A selected Order detail presents Confirm policy/ShipTo snapshot, reservation state, loaded quantity, per-Delivery source, held remainder, due dates and command outcomes.

- Explicit Confirm/Reserve/Load/Delivery commands, disabled/hidden according to current **server** authority, always rechecked server-side at commit.
- Read-only status can refresh; detect stale resource version. Keep stable command identity across timeout/lost ACK; offer outcome lookup and CASE/operator workflow rather than sending a new business command.
- Avoid optimistic success beyond committed server outcome; visually distinguish pending, conflict, fail-closed, held remainder, posted and unknown/requires reconciliation. Concrete status vocabulary/HTTP responses are a later increment contract.
- AR filtering by Customer, Delivery, due date, overdue, `asOf`; show pending/source coverage and export tenant-scoped with permission checks. Report query must reconcile with authoritative AR source, not a client-computed aging cache.
- No offline business write queue, no B_NO_CHANNEL activation and no real invoice/accounting dispatch.

## 7. Independent evidence and gate model

| Gate | Evidence needed before promotion | Current state |
|---|---|---|
| `C-PLAN` | C.0 10/10 decisions, bounded contract + reviewed scenario proposal, zero doc blockers, exact source-head required CI, docs-only PR | `REVIEW_PENDING` |
| `C-DEV` | **New explicit Owner authorization** for each C.1+ source scope, access/control boundaries, dependency and exception policy | `NOT_AUTHORIZED` |
| `C-PREPROD` | APP-02B `PREPROD_FOUNDATION_VERIFIED` including all **30 B.1** cases, 5 gates, 2 RUNTIME probes, #53/restore and independent reviewer requirements; approved real A commercial policy and separate runtime test authority | `BLOCKED` |
| `C-E2E` | Owner-approved final C scenario/fixture matrix; UI→API→SQL two physical PREPROD tenant test; independent expected/actual/diff and peer read/write negatives; fault/restart, TestRunId/source HEAD/image/DB digests, CI/regression, reviewer evidence | `NOT_RUN` |

At a future C-E2E execution record for **each approved case × applicable tenant/scope**: `CaseId`, `TestRunId`, source HEAD, build/image digest, environment ID, physical TenantDatabaseId/binding version, actor/session/permission epoch, policy/ShipTo/Order/Delivery source IDs and hashes, UTC and seller business date, fault ID, independently owned frozen expected with fixture SHA, SQL independently observed actual, signed difference, source/ledger/AR SQL before-after fingerprints, peer read/write probe, required UI/HTTP/OTel artifact links and SHA-256, reviewer identity/disposition, finding IDs. Redact PII/secrets. A response body alone does not certify a case.

**Reviewer separation:** preserve APP-02B.9's externally approved independent observer/Security/DBA trust requirements and any separately approved C E2E review rules. A solo Owner must not claim multiple human reviewers; an APP-02B.4/#59 exception is not inherited. A new Owner disposition is needed if the original B.9 assurance model cannot actually be met.

**Regression retention:** Existing APP-01E (cross-transaction SQL), APP-02A.4 (A11 synthetic), APP-02B bounded checks, full APP-00A, Node full+production audit, frontend/PWA/Playwright and new C suites must be tracked at the **same final reviewed source HEAD** when applicable. Required failures/skips cannot be silently waived. Historical one-off reruns do not close Issue #75.

## 8. Contract open items and decision requested at C.0 review

1. `C0-R01`: BR-03 uses 'Confirm ... giữ hàng' while Owner chose two commits. Freeze clear `CONFIRMED_UNRESERVED` semantics and accepted stock-hold point **before any C.2 implementation**; no rewrite of baseline.
2. `C0-R02`: Direct ShipTo fields/provenance and amendment eligibility/versioning are not yet enumerated or approved.
3. `C0-R03`: Approved effective policy values and seller timezone/business date are separately required. `UNSET` persists today; no fake price/N/credit values.
4. `C0-R04`: Exact reservation vs trip held/remainder and close/release/unload lifecycles require source-based definitions, not silent auto-release.
5. `C0-R05`: Physical reviewer/role separation and B.9 independent trust authority have not been waived; owner must plan genuine evidence custody/review.
6. `C0-R06`: Scenario list above is a **proposal**; fixture expected values and cases must be reviewed, versioned and independently frozen before execution; no new test-count PASS claim.

**Current disposition:** `10/10 OWNER_APPROVED / CONTRACT_IN_REVIEW / C-PLAN REVIEW_PENDING / ALL C PHYSICAL TESTS NOT_RUN / C.1+ NOT_AUTHORIZED`.
