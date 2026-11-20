# UI-04 — Sales A vertical-slice readiness, ADR and execution gates

**Status: OWNER_REQUESTED / CONTRACT_PREPARED / IMPLEMENTATION_BLOCKED / E2E_NOT_RUN.** This is a DOCS_ONLY preparation artifact, **not** an implemented business UI or an approval of APP-02C.1–C.6. Owner request: [20/11/2026](../decisions/owner-ui04-2026-11-20.md). Design baseline [ADOPTED](README.md); UI-01/02/03 synthetic labs are merged and are reusable as UX references, not C business acceptance.

## 1. Vertical slice selected and source contract

**Sales A / A_ONLINE:** Customer debtor and optional Outlet → registered or approved ad-hoc ShipTo → ConfirmAOrder → ReserveAStock → LoadManifest MAIN→TRIP → PostDelivery of actual quantity → per-Delivery Customer AR and due-date report. The desired path is [APP-02C.0 `1–4](../roadmap/app-02c0-a-online-e2e-contract.md) and the sequential C.1–C.6 [roadmap](../roadmap/app-02c-a-online-e2e.md).

Do not make the UI invent resolved C.0 source semantics. Current APP-01A reserves **before** Confirm, while C.0 requires Confirm→Reserve; current Load/right counters, partial-load re-entry and policy-validity-at-Confirm require separately approved compatibility work. A UI façade does not repair a broken SQL source contract.

### UI04-ADR-01 — implementation boundary

- **Reuse** the current React 19/TypeScript/Vite/PWA, Tailwind CSS v4 semantic tokens, Noto Sans, Light/Dark/System, density, native accessible controls, UI-02 lookup/editing contracts and UI-03 source/UNKNOWN recovery patterns.
- **Do not** choose a new grid/component/router library or implement a generic command bus in this preparation PR. C.5's future ADR shall evaluate a real workload with accessibility/keyboard/IME/size/security evidence before adding dependencies. UI-02's native demonstration is not a policy requiring all future large grids to be native.
- The future UI is **server-authoritative**: list and detail read from resource-scoped, current-epoch API; commands are separate explicit actions with stable identities and persisted outcomes. No client-side permission, stock, policy or accounting calculation becomes authority. Never construct a DB/tenant route from editable client parameters.
- **Online-first**: offline may display previously authorized, clearly dated read-only data according to an approved caching policy; it may not queue business writes. If no such policy exists, show an unavailable/offline state rather than imply trusted cache.
- **No live route** or fake POSTED/AR balance is added now. The future Sales A workspace route/name remains a C.5 implementation decision after its source/permission contract is approved.

## 2. Dependency-to-screen gate matrix

| Prerequisite | Blocking effect on UI-04 | Required closure/evidence |
|---|---|---|
| APP-02B foundation, G-B01..05, 30 physical B.1 cases | Default C.1+ source start, C-PREPROD and C.6 physical UI→API→SQL all blocked | Operator-controlled physical PREPROD evidence, independent roles, permissions/revocation, recovery and [B roadmap](../roadmap/app-02b-preprod-foundation.md) |
| C-DEV (explicit per-increment authority) | No source/SQL/API/business UI implementation from the C plan | New Owner approval explicitly naming C increment(s), environment, security, review and CI; a pre-B inert-source exception also needs an explicit timing override |
| C0-R01 | Cannot implement C.2 Confirm→Reserve state/preconditions; affects Load/Delivery UX | Approved BR-03 compatibility/state transition mapping and independent SQL review |
| C0-R02 | Cannot implement C.1 ShipTo direct validation/snapshot/amendment; affects order form | Approved address fields, authorization, provenance, amendment limits and immutable versions |
| C0-R03 | Cannot wire commercial policy resolution, terms, due dates and AR reports | Approved scope/priority/timezone/version contract; actual required policy must not be UNSET |
| C0-R04 | Cannot wire reservation→load→delivery/remainder/release ledger lifecycle | Independent SQL/ledger conservation and partial/repeated-load recovery review |
| C0-R05 | Cannot claim B.9/C-PREPROD independent physical acceptance | Real independent observers/Security/DBA, approved keys and custody or new appropriate Owner disposition |
| C0-R06 | Cannot execute/claim independent C test acceptance without frozen expected | Independent fixture/oracle review, versioned case/hash and signed expected/actual/difference |
| APP-02C C.1→C.4 | C.5 authoritative Customer/Order/Trip/Delivery/AR workspace cannot precede source services | Merge/review/CI on each separately authorized C increment; regressions and resource authorization |
| C-PREPROD + C.6 | No physical E2E verification | Two operator-controlled physical tenant DBs, current actor/epoch, SQL witnesses, negative/fault/restart and independent signoff |

## 3. Safe implementation order

1. **UI-04 readiness (this docs PR):** screen spec, ADR, blocker owner mapping and executable test plan; documentation consistency checks only. Keep runtime UNCHANGED.
2. **Owner/contract work, each separately reviewed:** close applicable C0-R01..06; secure B gates; obtain precise C-DEV authorization (or an explicit inert-source timing exception). Do not relabel this step as automatically authorized.
3. **C.1 / C.2 / C.3 / C.4 independently:** Customer/ShipTo, Confirm/Reserve, Load, then Delivery/AR authoritative commands, tests and bounded closeout per PR.
4. **C.5 = UI-04 code candidate:** one desktop-first Sales A list/detail command workspace with explicit source and outcome. Submit its own code PR and run browser, API/SQL, security and existing regressions on the exact final source HEAD.
5. **C.6 E2E:** independently freeze expected and observe actual per case/tenant, including fault, lost ACK, peer tenant denial and AR/stock/COGS/ledger; only after C-PREPROD. Do not call the full mixed AC-03 passed based on the A subset.

## 4. Evidence and release control

The [screen spec](ui-04-sales-a-screen.md) distinguishes source asOf, unknown/zero, held/delivered and intake ACK versus committed effects. The [verification matrix](ui-04-verification.md) defines planned cases. For every future PR record exact commit, reviewer, workflow/check runs, failures/reruns, browser traces and cross-tenant SQL/HTTP witnesses. Client screenshots or UI-03 browser storage are not an immutable server receipt.

This PR may earn only **DOCS_REVIEWED / DOCS_CI_VERIFIED**. It may **not** earn UI04_IMPLEMENTED, C_DEV_APPROVED, C_PREPROD_VERIFIED, C_E2E_VERIFIED, G-B/EV/P0 or production acceptance.

