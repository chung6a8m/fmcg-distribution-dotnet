# APP-02C — A_ONLINE Vertical E2E Roadmap (C.0–C.6)

> **CURRENT AUTHORITY — C.1–C.5 DEV AUTHORIZED (2026-11-22).** [OWN-APP02C-DEV-01..06](../decisions/owner-app02c-dev-first-2026-11-22.md) authorizes sequential source, owned DEV migrations, UI and integrated UI→API→SQL with synthetic data. [C.1 post-merge DEV closeout](../closeout/2026-11-22-app-02c1-dev.md) records the first implementation and its scoped evidence. C.6 physical acceptance and operational runtime admission retain their separate gates.

The [ten original business choices](../decisions/owner-app02c-2026-11-19.md) remain the C baseline. C.0 is `CONTRACT_APPROVED (DOCS_ONLY)` via [merged PR #77](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77). The new Owner decision changes timing/environment authority, not the locked business rules or historical test results.

Refs: [C.0 business/API/UI/oracle contract](app-02c0-a-online-e2e-contract.md) · [APP-02 master roadmap](app-02.md) · [APP-02B PREPROD gates](app-02b-preprod-foundation.md) · [B.1 physical matrix](../security/app-02b1-acceptance-evidence.md) · [B.9 protocol](app-02b9-independent-preprod-oracle.md).

## 1. Scope and dependency chain

The implementation sequence remains **C.1 → C.2 → C.3 → C.4 → C.5**, one independently reviewable increment/PR at a time. Start C.1 with its data contract and minimum DEV composition. Each successor requires the predecessor functionality it consumes, its own reviewed business semantics and meaningful verification; it does not wait for the entire operations backlog.

The Owner's 2026-11-22 decision is standing authorization for this sequence in the named DEV environment. **`PREPROD_FOUNDATION_VERIFIED` is no longer a prerequisite for C.1–C.5 development or synthetic integrated DEV runs.** Do not ask for the same blanket authorization again. Work on C0-R01–R04 is part of the owning increment, before dependent behavior is accepted; unresolved unrelated rows do not block its start. See [the scoped dispositions](app-02c0-a-online-e2e-contract.md#8-contract-review-dispositions-and-blocking-gates).

The initial DEV target uses existing nonce-owned LocalDB protections and real API/SQL code. The [C.1 contract and launcher](app-02c1-customer-shipto.md) supply runtime and browser wiring. The [DEV handoff](app-02c-dev-handoff.md) carries requirements into subsequent increments. The [operations debt register](app-02c-operations-debt.md) records the re-entry triggers for Vault, LAB topology and physical reviewer staffing.

**Physical acceptance stays separate:** after the business flow exists, entry to C.6 still needs C-PREPROD, applicable B physical evidence and approved operational policies, or a separately reviewed replacement acceptance contract. Source/DEV results do not earn those states or permit real data, production or Pilot.

## 2. Increment specifications and current progress

| Increment | Deliverables in its implementation PR | Independent exit evidence | Status on 2026-11-22 |
|---|---|---|---|
| **C.0 — Owner Decisions & E2E Contract** | Frozen ten decisions, bounded business/API/UI/data contract, proposed independent scenario matrix, evidence schema and this roadmap. Scope limited to Markdown in `docs/` plus docs-only README/APP-02 cross-reference updates. | `C-PLAN`: reviewer approves authority consistency, BR/POL/AC lineage, no `business/` edits, no undocumented implementation authorization; exact PR final-HEAD required CI and merge. | Docs only; disposition in **[PR #77](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77)** |
| **C.1 — Customer, Outlet & Flexible ShipTo** | [R02 contract and implementation](app-02c1-customer-shipto.md), Customer debtor and 0..n Outlet, registered/ad-hoc ShipTo validation, immutable selection/revision, owned DEV launcher, migrations, server-authorized API and Customer/ShipTo screen. | Real UI→API→SQL and two-tenant readback, scoped AC-02 coverage, authorization, replay, rollback, stale version and mutation negatives. C.2/C.4 own Confirm/Delivery assertions. | `MERGED / DEV_INTEGRATION_VERIFIED / CI_VERIFIED` · [closeout](../closeout/2026-11-22-app-02c1-dev.md) |
| **C.2 — A Confirm & Reservation** | [C.2 DEV contract](app-02c2-confirm-reserve.md) records R01 and the applicable portions of R03/R04, integrates C.1 ShipTo, and delivers drafts, Confirm and Reserve in `/sales/orders`. | Confirmed-unreserved/partial/full states, Reserve retry without reconfirmation, legacy Load fence, policy `UNSET` denial, replay/conflict, A/B competition, zero AR/COGS and two-tenant SQL readback. | `IMPLEMENTED / LOCAL_DEV_VERIFIED / CI_PENDING` · [closeout](../closeout/2026-11-22-app-02c2-dev.md) |
| **C.3 — Atomic Load Manifest** | Close applicable R04 logistics semantics; adapt APP-01B.2 MAIN→TRIP movement with reservation binding, loaded/warehouse remainder, partial-load uniqueness and recovery. Add the usable manifest/Load UI; do not debit the delivery-consumed counter at Load. | Stock/value and entitlement reconciliation, partial/repeated/competing-load guards, no AR/COGS, whole manifest rollback, lost ACK, wrong tenant, concurrency and UI/API/SQL evidence. | `DEV_AUTHORIZED / AFTER_C2 / NOT_STARTED` |
| **C.4 — Partial Delivery & Due-Dated AR** | Apply reviewed R03/R04 semantics to APP-01C.3: one delivery consumption, source-bound stock/COGS/AR, `HELD` remainder, seller-local due date from Confirm terms and reconciliation. Add usable partial-delivery and resulting AR views. | AC-03 A subset load40/deliver25/held15; AC-08 master/policy changes without historical rebind; current authority; independently checked AR/stock/COGS; overdelivery/race/rollback/CASE and UI/API/SQL negatives. | `DEV_AUTHORIZED / AFTER_C3 / NOT_STARTED` |
| **C.5 — Business Workspace & Reporting** | Integrate and refine the C.1–C.4 screens in the existing app; command outcome recovery, snapshot/audit views and SQL-authoritative Customer/Delivery/due/`asOf` AR reporting. Complete the full A flow in DEV. | Full scoped DEV UI→API→SQL flow, permissions, lost ACK/no double-command, `asOf`/pending correctness, keyboard/theme/density/browser/PWA checks and SQL reconciliation. No offline writes. | `DEV_AUTHORIZED / AFTER_C4 / NOT_STARTED` |
| **C.6 — Independent Physical E2E Acceptance** | Freeze and execute approved scenario/expected matrix across full UI→API→SQL, two operator-owned physical PREPROD tenants and negative/fault/restart suites; independently observed SQL/oracle; closeout. | `C-E2E` only after `C-PREPROD`; exact reviewed HEAD and full required CI, independent expected/actual/difference, witnesses and Owner signoff. Remains A-scope only. | `PROPOSED / NOT_AUTHORIZED / NOT_RUN` |

Each C.1–C.5 increment has its own exit review and final-source CI under the standing DEV authorization. C.6 needs its separate physical admission decision. Neither C.5 DEV completion nor C.6's A-only result implies APP-02D/E, production or Pilot readiness.

## 3. Development and physical gates

| Gate | Required entry/exit conditions | Current gate state |
|---|---|---|
| **`C-PLAN` — Contract Approved** | Ten decisions captured; [C.0 contract](app-02c0-a-online-e2e-contract.md) and roadmap reviewed against canonical Business Baseline and B dependencies; C0-R01..06 resolved or explicitly **DEFERRED / BLOCKING** with affected gates, closure artifacts and approving roles; docs-only final diff, reviewer approval, final source-head required CI and merge. Deferred rows remain open for future work. | See [PR #77 review/CI/merge record](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/77); no approval inferred from planning alone |
| **`C-DEV` — Implementation and synthetic DEV authorization** | [2026-11-22 Owner decision](../decisions/owner-app02c-dev-first-2026-11-22.md) authorizes C.1–C.5 source/migrations/UI and integrated execution in bounded DEV. Resolve applicable semantics in the owning increment and preserve security, correctness and reviewed CI. | `OWNER_APPROVED / DEV_AUTHORIZED`; C.1 merged |
| **Integrated DEV verification — per increment** | Real scoped UI/API/SQL path, versioned reviewed expectations, negative cases, source and environment lineage, independent review and actual results. | C.1 verified in synthetic DEV and CI. C.2 verified in local synthetic DEV. C.3–C.5 `NOT_RUN`. |
| **`C-PREPROD` — Physical A Test Runtime Admission** | APP-02B `PREPROD_FOUNDATION_VERIFIED`: **30/30 B.1** cases all PASS, 5/5 G-B gates all PASS, RUNTIME-01/02, distinct physical A/B tenant DB and independent Security/DBA/observer evidence; #53 restore-safe revocation and security findings closed/accepted for this scope, recovery/alerts real; APP-02C commercial policies approved and a separate Owner authorization for nonproduction A test runs. | `BLOCKED / NOT_VERIFIED` |
| **`C-E2E` — Independent A_ONLINE E2E** | Authorized physical UI→API→SQL run with approved case/expected fixture, source/actor/policy/stock/COGS/AR due-date lineage, peer-tenant denial and fault/restart/lost ACK, signed independent actual/expected/difference, exact CI/review/head/image/DB/TestRunId, zero blocking findings, documented Owner acceptance. No production/Pilot promotion. | `NOT_RUN` |

**APP-02B gate status cannot be inferred from source or DEV execution.** B.9's operator/independent-review requirements apply to its physical acceptance and C-PREPROD/C.6, not C.1–C.5 DEV entry. The 2026-11-22 decision supplies new DEV authority; it does not inherit or broaden old source-only risk exceptions.

## 4. Business traceability by increment

| Business constraint / evidence | Source of truth | Planned C ownership / limitation |
|---|---|---|
| AC-02, BR-02, POL-02: Customer debtor, optional Outlet, ShipTo snapshot | `business/acceptance.md`, `business/business-rules.md`, `business/policies.md` | C.1 & C.5; actual EV-02 still pending until physical witness |
| AC-03 **A subset**: A40 / A25 / held15, AR_A375; no AR/COGS at Load | same sources + existing APP-01A/B/C/E contracts | C.2–C.4 + C.6; B31/B20/custody/fund/full COGS450 remain D scope and cannot be falsely counted as C PASS |
| AC-08, BR-10, POL-10: terms snapshot, delivery-local N days | same sources | C.2, C.4, C.5, C.6; approved actual seller timezone/terms required |
| Tenant, revoke, restore/current actor and policy | BR-01/17, APP-02B.3–B.7 | C.1–C.5 retain current server/SQL authority in DEV; physical restore/independent B evidence is assessed at C-PREPROD/C.6. Synthetic credentials/targets never authorize an operational tenant. |
| Idempotency and recovery | BR-16, APP-01E, APP-02A | C.2–C.6; retain immutable command/source identity and negative cases |
| Reporting source/asOf/unknown | BR-23, POL-23 | C.4–C.6; no dual-AR ledger or default zero for missing external data |
| G-STOCK, G-CUT, G-IDEMP, G-LEDGER, G-INV; EV-01..15; full AC list | Product Verification scope | Outside C acceptance; remain `NOT_VERIFIED` for production/Pilot |

## 5. PREPROD blockers and CI risk ledger — snapshot 2026-11-19

**Historical snapshot retained for provenance, not current GitHub issue status or C-DEV admission.** Some issues/PRs below have since closed. Consult current records before acting; a GitHub closure alone does not prove physical acceptance. For current development classification and re-entry triggers use the [operations debt register](app-02c-operations-debt.md). No old issue status below reinstates the superseded blanket development prerequisite.

| Ref | Verified GitHub state | Gate impact / required next action |
|---|---|---|
| [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53) / [PR #74](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/74) | Issue OPEN; PR DRAFT | No restored-epoch security claim. Independently review/finish role/procedure/physical bridge and current revocation guard; block C-PREPROD. |
| [#56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56) | OPEN | Execute all 30 B.1 physical cases and 5 gates plus independent roles, restore/alert/SQL witnesses, RPO/RTO targets. Block C-PREPROD. |
| [#75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75) | OPEN | Diagnose A11-09 restart HTTP 500 and LocalDB intermittent bootstrap; a successful retry is not a root-cause fix. Safeguard C regression; [#68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68) tracks the earlier A11-09 failure on B.6 HEAD. |
| [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) / [PR #61](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/61) | CLOSED via limited risk acceptance, **NOT_PATCHED**; exact-source final CI 10/10 workflows SUCCESS on last attempt | Decision applies only source/ephemeral CI, not C frontend artifact publication or physical PREPROD. Review actual risk, expiry 2026-12-19 UTC and require separate authorization/patch for expanded scope. |
| [PR #73](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/73) | OPEN docs-only B.9 closeout | Reconcile #59 historical FAIL/open wording vs newer closed/scoped-accepted state and preserve historical failed attempts. No C status promotion. |
| [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52), [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54), [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55) | OPEN — real PREPROD IAM/host/audit dependencies | Actual operator Identity SQL/refresh, host TLS/Vault/RUNTIME-01/02, immutable identity/security audit, approved MFA/key governance and independent physical evidence must be completed before C-PREPROD. |
| [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), [#64](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/64), [#65](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/65), [#67](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/67) | Security/DBA/physical evidence required; consult current issue states before C-PREPROD | SQL finalizer privilege retirement, physical tenant SQL DML boundary, Maker/Checker provenance and B.6 live CASE/policy binding must be independently established. |

The new DEV decision changes when application work may proceed. It is **not** physical completion or a source-level security waiver; concrete CI/security defects still require scoped assessment.

## 6. PR sequencing and merge/closeout protocol

1. **DEV-first documentation amendment:** recorded the approved timing, updated the C gates and added the handoff and operations debt register.
2. **C.1 implementation:** use the [C.1 contract and launcher](app-02c1-customer-shipto.md) and [post-merge closeout](../closeout/2026-11-22-app-02c1-dev.md).
3. **C.2–C.5:** continue sequentially under the same DEV authorization. Each PR owns its applicable contract detail, UI/API/SQL behavior and operations debt updates. A material business ambiguity pauses only its dependent behavior; record resolved technical choices and ask only for genuinely new Owner business decisions.
4. For each implementation PR: scoped AC/BR/POL expected results, explicit command/source semantics, two-tenant SQL/negative coverage when applicable, meaningful UI integration evidence and relevant APP-01E/APP-02A/B regressions. Keep required APP-00A/frontend/PWA/dependency checks on the final reviewed HEAD. Do not turn failed/skipped required checks into PASS or claim a rerun fixed an unresolved defect.
5. Record source SHA, environment/data scope, artifact hashes, TestRunId, expected/actual result, reviewer disposition, real `PASS/FAIL/NOT_RUN/BLOCKED`, remaining debt and next action in the increment's substantive closeout. Update this roadmap's progress in that same PR. No acknowledgement-only documentation chain and no full AC-03/EV/P0 claim from an A slice.
6. **C.6 physical acceptance:** separately satisfy C-PREPROD, re-evaluate [operations debt](app-02c-operations-debt.md), freeze the physical C fixture and obtain the applicable real two-tenant operator/reviewer evidence. An unmet physical reviewer requirement blocks that physical claim; it does not revoke ongoing C.1–C.5 DEV authorization.

## 7. Status vocabulary

| State | Meaning on this amendment |
|---|---|
| `C0_CONTRACT_APPROVED_DOCS_ONLY` | C.0 baseline reviewed and merged in PR #77; original business decisions retained. |
| `C_DEV_AUTHORIZED` | Owner approved C.1–C.5 source/migrations/UI and bounded integrated DEV execution on 2026-11-22. |
| `NOT_STARTED` / `IN_PROGRESS` / `IMPLEMENTED` | Per-increment implementation progress. C.1 is merged, C.2 is implemented and C.3–C.5 are `NOT_STARTED`. |
| `DEV_INTEGRATION_NOT_RUN` / scoped `DEV_INTEGRATION_VERIFIED` | Actual development UI/API/SQL evidence. C.1 has scoped local and CI verification on the merged source tree. |
| `C_PREPROD_NOT_VERIFIED` / `C_E2E_NOT_RUN` | Physical operational admission and C.6 results remain unearned. A real SQL DEV run is still a DEV result. |
| `PRODUCTION_PILOT_GO_LIVE_NOT_AUTHORIZED` | Separate Product Verification, EV/P0 and Owner/Tenant admission remain required. |

The old blanket `C1_C6_NOT_AUTHORIZED` is historical. It must not be used to reject C.1–C.5 DEV work covered by the new decision; C.6 keeps its separate physical scope.
