# APP-02C — A_ONLINE Vertical E2E Roadmap (C.0–C.6)

> **OWNER-APPROVED ROADMAP PROPOSAL — DOCS-ONLY PR REVIEW PENDING (2026-11-19).** Interview: [OWN-APP02C-01..10](../decisions/owner-app02c-2026-11-19.md) all `OWNER_APPROVED`, **Option 1**. This approves **planning structure only**. **Only APP-02C.0 documents are authorized**; C.1–C.6 source, SQL migrations, UI changes and PREPROD A_ONLINE effect routes remain `NOT_AUTHORIZED`. C.0 itself is `DOCS_PR_REVIEW_PENDING`, not an accepted/merged PR.

Refs: [C.0 business/API/UI/oracle contract](app-02c0-a-online-e2e-contract.md) · [APP-02 master roadmap](app-02.md) · [APP-02B PREPROD gates](app-02b-preprod-foundation.md) · [B.1 physical matrix](../security/app-02b1-acceptance-evidence.md) · [B.9 protocol](app-02b9-independent-preprod-oracle.md).

## 1. Scope and dependency chain

```text
C.0 Owner decisions / contract / roadmap                  [DOCS ONLY; NOW AUTHORIZED]
       |
       +-- C-PLAN (reviewed docs / exact-HEAD CI)
       |
       +-- NEW C-DEV OWNER DECISION ---------------------- [C.1+ NOT AUTHORIZED]
                |
                +-- C.1 Customer / Outlet / Flexible ShipTo
                        |
                        +-- C.2 A Confirm / approved policy / Reserve
                                |
                                +-- C.3 Atomic LoadManifest MAIN -> TRIP
                                        |
                                        +-- C.4 Partial Delivery / AR / due dates
                                                |
                                                +-- C.5 UI Workspace / AR Reporting
                                                        |
                                       APP-02B G-B01..05 + C-PREPROD + actual policies
                                                        |
                                                        +-- C.6 Independent physical E2E
                                                                |
                                                           C-E2E (A-only verified)
```

The plan is sequential in the sense of one independently reviewable **increment and PR at a time**; a future separately scoped source-development authorization may allow inert code/CI work before full physical B completion, but **no C code or migration authorization exists today**. Real runtime testing/admission requires `C-PREPROD` and its separate Owner decision; no implicit waiver from B source-only merges.

## 2. Proposed increment specifications (7 total, no implementation underway)

| Increment | Deliverables in a **future separately authorized PR** | Independent exit evidence | Status today |
|---|---|---|---|
| **C.0 — Owner Decisions & E2E Contract** | Frozen ten decisions, bounded business/API/UI/data contract, proposed independent scenario matrix, evidence schema and this roadmap. Scope limited to Markdown in `docs/` plus *proposed* docs-only README/APP-02 cross-reference updates. | `C-PLAN`: reviewer approves authority consistency, BR/POL/AC lineage, no `business/` edits, no undocumented implementation authorization; exact PR final-HEAD required CI. | **`DOCS_PR_REVIEW_PENDING`** |
| **C.1 — Customer, Outlet & Flexible ShipTo** | Authoritative Customer debtor and 0..n Outlet, registered/ad-hoc ShipTo validation, immutable snapshots and auditable amendment versioning, API/DTO/UI contract test stubs as applicable. | AC-02 focused positive + unauthorized address/tenant, stale amendment, master-change and snapshot mutation negatives; two SQL tenants where applicable. | `PROPOSED / NOT_AUTHORIZED` |
| **C.2 — A Confirm & Reservation** | Integrate existing APP-01A Confirm/Reserve commands with B.3 actor, B.4 binding, B.5 revocation fence, B.6 approved versioned policy and frozen snapshots; explicit confirmed-unreserved states. | Policy `UNSET` denial, per-command idempotency, A/B stock non-overlap and race; zero AR/COGS at Confirm/Reserve; two-tenant negative; never grant real parameters by fixture. | `PROPOSED / NOT_AUTHORIZED` |
| **C.3 — Atomic Load Manifest** | Reuse APP-01B.2 Load transaction as server-authoritative API/operation; reservation consumption, MAIN→TRIP stock/value ledger, partial manifest semantics and recovery. | Exact SQL before/after stock/value, actor/tenant/trip guards, no AR/COGS, whole requested manifest rollback, lost ACK, wrong tenant, concurrency. | `PROPOSED / NOT_AUTHORIZED` |
| **C.4 — Partial Delivery & Due-Dated AR** | Reuse APP-01C.3 per-Delivery posting, source-bound stock/COGS/AR, explicit `HELD` remainder, seller-local terms N snapshot, due-date and correction/reconciliation. | AC-03 A subset only, AC-08 master-mutation and per-delivery due dates, independently checked AR/stock/COGS, overdelivery/race/rollback/CASE negatives. | `PROPOSED / NOT_AUTHORIZED` |
| **C.5 — Business Workspace & Reporting** | Desktop-first Customer/Order/Trip/Delivery screens using existing web app; explicit commands, persisted outcome lookup, frozen snapshot/audit displays; SQL-authoritative Customer/Delivery/due/`asOf` AR reports. | UI→API behavior, permissions, lost ACK/no double-command, asOf/pending correctness, accessibility/browser/PWA/Playwright baseline preserved; no offline writes. | `PROPOSED / NOT_AUTHORIZED` |
| **C.6 — Independent Physical E2E Acceptance** | Freeze and execute approved scenario/expected matrix across full UI→API→SQL, two operator-owned physical PREPROD tenants and negative/fault/restart suites; independently observed SQL/oracle; closeout. | `C-E2E` only after `C-PREPROD`; exact reviewed HEAD and full required CI, independent expected/actual/difference, witnesses and Owner signoff. Remains A-scope only. | `PROPOSED / NOT_AUTHORIZED / NOT_RUN` |

**Every later increment has a fresh authorization and exit review.** C.1 may not start merely because C.0 is merged. C.6's separate status will not imply APP-02D/E, production or Pilot readiness.

## 3. Four gates (proposed contract, not gate results)

| Gate | Required entry/exit conditions | Current gate state |
|---|---|---|
| **`C-PLAN` — Contract Approved** | Ten decisions captured; [C.0 contract](app-02c0-a-online-e2e-contract.md) and roadmap reviewed against canonical Business Baseline and B dependencies, issues C0-R01..06 dispositioned (resolved or explicitly blocked/deferred), docs-only final diff, reviewer approval, final source-head required CI. | `REVIEW_PENDING` |
| **`C-DEV` — Explicit Implementation Authorization** | New, scope-bound Owner approval before *any* C.1+ code/DB/UI work, with named increment(s), source-only vs physical permitted environment, security/tenant/business constraints, PR/reviewer/CI conditions and prohibitions. No inherited B.2/B.4/B.5/B.6/B.7/B.8/B.9 debt exceptions. | `NOT_AUTHORIZED` |
| **`C-PREPROD` — Physical A Test Runtime Admission** | APP-02B `PREPROD_FOUNDATION_VERIFIED`: **30/30 B.1** cases all PASS, 5/5 G-B gates all PASS, RUNTIME-01/02, distinct physical A/B tenant DB and independent Security/DBA/observer evidence; #53 restore-safe revocation and security findings closed/accepted for this scope, recovery/alerts real; APP-02C commercial policies approved and a separate Owner authorization for nonproduction A test runs. | `BLOCKED / NOT_VERIFIED` |
| **`C-E2E` — Independent A_ONLINE E2E** | Authorized physical UI→API→SQL run with approved case/expected fixture, source/actor/policy/stock/COGS/AR due-date lineage, peer-tenant denial and fault/restart/lost ACK, signed independent actual/expected/difference, exact CI/review/head/image/DB/TestRunId, zero blocking findings, documented Owner acceptance. No production/Pilot promotion. | `NOT_RUN` |

**APP-02B gate status cannot be inferred from the source-only B.9 validator.** B.9's separate operator and independent review requirements are prerequisites, not replaceable by the C oracle or a prior solo-Owner code review exception.

## 4. Business traceability by increment

| Business constraint / evidence | Source of truth | Planned C ownership / limitation |
|---|---|---|
| AC-02, BR-02, POL-02: Customer debtor, optional Outlet, ShipTo snapshot | `business/acceptance.md`, `business/business-rules.md`, `business/policies.md` | C.1 & C.5; actual EV-02 still pending until physical witness |
| AC-03 **A subset**: A40 / A25 / held15, AR_A375; no AR/COGS at Load | same sources + existing APP-01A/B/C/E contracts | C.2–C.4 + C.6; B31/B20/custody/fund/full COGS450 remain D scope and cannot be falsely counted as C PASS |
| AC-08, BR-10, POL-10: terms snapshot, delivery-local N days | same sources | C.2, C.4, C.5, C.6; approved actual seller timezone/terms required |
| Tenant, revoke, restore/current actor and policy | BR-01/17, APP-02B.3–B.7 | C.1–C.6; B G-B01..05 and #53 dependency; do not reuse synthetic JWT/LocalDB as real auth |
| Idempotency and recovery | BR-16, APP-01E, APP-02A | C.2–C.6; retain immutable command/source identity and negative cases |
| Reporting source/asOf/unknown | BR-23, POL-23 | C.4–C.6; no dual-AR ledger or default zero for missing external data |
| G-STOCK, G-CUT, G-IDEMP, G-LEDGER, G-INV; EV-01..15; full AC list | Product Verification scope | Outside C acceptance; remain `NOT_VERIFIED` for production/Pilot |

## 5. PREPROD blockers and CI risk ledger — snapshot 2026-11-19

| Ref | Verified GitHub state | Gate impact / required next action |
|---|---|---|
| [#53](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/53) / [PR #74](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/74) | Issue OPEN; PR DRAFT | No restored-epoch security claim. Independently review/finish role/procedure/physical bridge and current revocation guard; block C-PREPROD. |
| [#56](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/56) | OPEN | Execute all 30 B.1 physical cases and 5 gates plus independent roles, restore/alert/SQL witnesses, RPO/RTO targets. Block C-PREPROD. |
| [#75](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/75) | OPEN | Diagnose A11-09 restart HTTP 500 and LocalDB intermittent bootstrap; a successful retry is not a root-cause fix. Safeguard C regression; [#68](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/68) tracks the earlier A11-09 failure on B.6 HEAD. |
| [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) / [PR #61](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/61) | CLOSED via limited risk acceptance, **NOT_PATCHED**; exact-source final CI 10/10 workflows SUCCESS on last attempt | Decision applies only source/ephemeral CI, not C frontend artifact publication or physical PREPROD. Review actual risk, expiry 2026-12-19 UTC and require separate authorization/patch for expanded scope. |
| [PR #73](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/73) | OPEN docs-only B.9 closeout | Reconcile #59 historical FAIL/open wording vs newer closed/scoped-accepted state and preserve historical failed attempts. No C status promotion. |
| [#52](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/52), [#54](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/54), [#55](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/55) | OPEN — real PREPROD IAM/host/audit dependencies | Actual operator Identity SQL/refresh, host TLS/Vault/RUNTIME-01/02, immutable identity/security audit, approved MFA/key governance and independent physical evidence must be completed before C-PREPROD. |
| [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), [#64](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/64), [#65](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/65), [#67](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/67) | Security/DBA/physical evidence required; consult current issue states before C-PREPROD | SQL finalizer privilege retirement, physical tenant SQL DML boundary, Maker/Checker provenance and B.6 live CASE/policy binding must be independently established. |

The prior C.0 interview and this draft are **not** a replacement for these physical dependencies or a source-level security waiver.

## 6. Proposed PR sequencing and merge/closeout protocol

1. **C.0 docs PR (first, only currently authorized increment):** propose changes to exactly the three new Markdown files in this review package; optionally update `docs/roadmap/app-02.md` and README to add *links and honest current states* in the same docs-only PR, provided independently reviewed. No C source file, migration, workflow, test, `business/` or lock changes. Do not copy this bundle's review memo into the repo by default.
2. At C.0 docs review, **freeze authority**: 10 decisions; distinguish `OWNER_APPROVED`, `C-PLAN REVIEW_PENDING`, `CONTRACT_APPROVED` and physical verification. Verify relative links and cross-document states, including #59 and #73. Never call docs-only CI a PREPROD gate pass.
3. Before C.1, request **new Owner decision** with exact allowed source/CI and permitted physical environment. Document risk/security and dependencies; no inherited authorization from C.0.
4. For each later PR: one increment; scoped AC/BR/POL acceptance, explicit source-identity semantics, two-tenant independent SQL/negative coverage when applicable, no required skips, no rerun laundering, full APP-00A + frontend/PWA/Playwright and relevant B/APP-01E/APP-02A regressions on exact final reviewed HEAD. Updated HEAD invalidates previous review/CI evidence.
5. Keep per-PR closeout with source SHA, artifact SHA-256, TestRunId, independent reviewer/Owner disposition, actual `PASS/FAIL/NOT_RUN/BLOCKED`, outstanding dependencies, accepted exceptions and next action. Never infer full AC-03/EV/P0 from a C slice.
6. Before C.6 physical acceptance, satisfy `C-PREPROD`; freeze independent C fixture, obtain real two-tenant PREPROD operator evidence and preserve B.9 externally trusted reviewer/observer requirements. If solo-dev staffing cannot meet required independence, **halt that gate** pending explicit appropriate Owner decision and substantive controls—not a self-issued independent signature.

## 7. Future C closeout/status vocabulary (proposal)

- `OWNER_APPROVED_DECISIONS_10_OF_10` — the ten interview choices have been made.
- `C0_DOCS_PR_REVIEW_PENDING` — three proposed Markdown artifacts exist, **in docs-only PR / not merged**.
- `C0_CONTRACT_APPROVED_DOCS_ONLY` — **future conditional**, after approved PR review, exact CI and merge.
- `C1_C6_NOT_AUTHORIZED` — no permission to implement/execute later increment.
- `C_PREPROD_NOT_VERIFIED` — no physical runtime admission and no real A_ONLINE E2E PASS.
- `C_E2E_NOT_RUN` — no independent real UI→API→SQL acceptance yet.
- `PRODUCTION_PILOT_GO_LIVE_NOT_AUTHORIZED` — separate Product Verification, EV/P0 and Tenant/Owner gates remain required.

**No future label is earned automatically by drafting this roadmap or by closing the C.0 docs PR.**
