# UI-04 Screen Spec — Sales A: Order → Delivery → Customer AR

**Status: SPECIFIED_DRAFT / IMPLEMENTATION_BLOCKED / ALL_SCREEN_EVIDENCE_NOT_RUN.** Candidate workspace, not a deployed route. Applies [screen template](templates/screen-spec.md), adopted [foundations](foundations.md), [components](components.md), [interaction](interaction-patterns.md), [UX-B01…B12](business-patterns.md), and [C.0 A_ONLINE contract](../roadmap/app-02c0-a-online-e2e-contract.md). See [readiness gates](ui-04-readiness.md).

## 1. Task, actor and scope

Primary actor: authorized Sales/order operator preparing an A order. Warehouse/Trip writer and Delivery poster act under **their independently verified assignments**; Customer AR readers require separate reporting permission. A menu role cannot confer these rights. No client-requested tenant/DB, and no System Admin implicit access to business data.

Task: find a Customer/debtor, prepare valid ShipTo and order, explicitly Confirm then Reserve; trace when warehouse Loads, actual Delivery posts, and AR source/due-date is available. The list/detail workspace must show incomplete work and next actor/action without pretending the Sales actor holds every write permission.

Desktop-first Compact: body 14/20, table 13/18, control min32, read row min36; Comfortable context may use 40/44. Field layout (16/24, target48) requires its own acceptance/authorization and is **not** implemented by this screen spec.

## 2. Proposed screen anatomy (C.5 after authoritative C.1–C.4)

**List pane:** filters by owned scope, Customer, status, source asOf and date, selection and stable row identity. Columns prioritize order ID, Customer debtor, optional Outlet, ShipTo summary, quantity/units, Confirm/Reserve/Load/Delivery state, outstanding next action and source timestamp. Totals explicitly name scope and n/N completeness; missing and actual zero remain distinct.

**Detail pane:** (a) debtor and ShipTo identity/snapshot; (b) line items, canonical base UoM, approved price/discount/tax/terms source at Confirm; (c) side-by-side reservation, MAIN/TRIP load, delivered and HELD remainder by source; (d) discrete command area with one clearly prioritized eligible action, separate confirmations and preflight/server result; (e) per-Delivery AR and due-date, source link, allocations/corrections and reconciled state. Never conflate a stock physically on a trip with available-to-sell stock.

**Context:** active tenant membership and authorized assignment **from server claims**, chosen Customer and immutable source/version. Preserve selected row/filter/scroll across list↔detail only if the same resource/tenant/permission remains authorized. On tenant switch or epoch revocation clear sensitive data, selection, pending request details and retained caches before fetching under new authority.

**Navigation:** future route/menu requires C.5 ADR, resource permission guard and deep-link reload tests. Do not ship a dummy route or URL in this PR.

## 3. Source, identity and freshness

| Area | Required authority | Screen meaning / empty and stale behavior |
|---|---|---|
| Customer / Outlet / ShipTo | Current-tenant authorized Customer service; immutable ShipTo at Confirm | Customer is debtor; Outlet optional; unvalidated direct ShipTo never silently becomes valid |
| Price, terms, policy | Approved effective policy version/snapshot anchored at Confirm | Explicit source/version, seller business date; missing/UNSET blocks Confirm; master updates do not reprice history |
| Reservation | SQL reservation and B quota/stock-right source | Show confirmed-but-unreserved distinctly from full/partial reservation; do not Load more than valid hold |
| Load and trip stock | Atomic LoadManifest source + warehouse/TRIP ledger | MAIN→TRIP is custody/stock movement, not AR/COGS/revenue; partial load retains separately sourced remainder |
| Delivery | One posted Delivery source and actual business date | Per-Delivery effect: stock/COGS/AR from authoritative server; HELD remainder stays visible, not auto-delivered |
| AR | SQL source-bound Customer/Delivery ledger with allocations and corrections | Label currency, source, asOf, due date, pending/reconciliation; no secondary browser-computed ledger |
| Command outcome | Durable outcome lookup under current resource permission | Same command ID/payload, ACK distinct from POSTED, UNKNOWN never inferred failed/rejected |
| KPIs and list totals | Authoritative query returning scope, asOf and completeness | Known partial sum ≠ complete total; no data ≠ actual zero; stale asOf not replaced with device clock |

No synthetic example (price 15, terms 7/14, credit threshold, stock 40, etc.) can be copied into tenant configuration.

## 4. Commands, states and next actions

Each named command is a **future separately idempotent server operation**, not a browser reducer transition.

| State / result (logical label only; final API mapping pending) | UI and allowed continuation |
|---|---|
| DRAFT | Editable only with current authority; Validate/Preview (read-only) does not commit |
| CONFIRMED_UNRESERVED | Explicitly distinguish completed Confirm from **uncommitted** Reserve; allow Reserve only after new server check; **no Load** |
| PARTIALLY_RESERVED / RESERVED | Display exact held quantity and source; Load only validated requested reserved quantity by authorized trip writer |
| LOADED_PARTIAL / LOADED | Display MAIN/TRIP ledger transition and remaining source; no AR/revenue/COGS inferred from load |
| DELIVERED_PARTIAL / HELD_REMAINDER | Posted delivery has one authoritative effect; display delivered units vs unsold HELD; never silently release or sell remainder |
| PENDING / UNKNOWN / ACK | Freeze immutable command ID and canonical request; query outcome **using same ID**, not retry with another ID; ACK alone is not business posting |
| POSTED / REJECTED / CONFLICT | Read server-authoritative result and linked source; conflict locks bypass and directs reconciliation/CASE |
| NO_PERMISSION / REVOKED / OFFLINE | No submit; redact unauthorized source/payload; offline queues no business commands; re-authorize before next action |
| ERROR / STALE / INCOMPLETE | Keep last reliable source asOf and reason; do not substitute guessed financial zero, success or timestamp |

No automatic advancement to Reserve/Load/Delivery after a previous command: every effect is an explicit user action with independent identity and authorization. A physical Delivery that occurred but cannot be posted must be recorded by an authorized occurred-event/CASE mechanism once contracted, **not** treated as never happened.

## 5. Data entry, keyboard and accessibility

Use identity-based Customer/SKU lookup: input text alone is not a selected ID; preserve first typed character, caret and IME composition; Enter commits a highlighted valid identity, Tab moves to next logical field and never posts a business command. In the grid, distinguish cell navigation from edit mode; arrow keys in input edit caret/value, Escape cancels uncommitted buffer (not a confirmed physical effect). Validate quantity/UoM/precision and immutable version at server; show field-local messages while retaining input. Guard against stale order revisions and lost-ACK double-submit. Long Vietnamese names and monetary values wrap/expand, never clip focus/editor or silently truncate business evidence.

Focus moves to error summary/field on invalid action, stays stable after source refresh, and returns to invoker after modal close. Use clear ARIA names/status for badges and pending/UNKNOWN; color is not sole indicator. Confirmation of actually received/given/posted physical facts starts **unchecked**, not pre-acknowledged by a sample screenshot.

## 6. Layout, theme, PWA and rights

At desktop show compact table + adjacent detail where viewport permits; on narrow screens use one-column list→detail page/drawer only under an approved Field/small-screen review, with safe area and sticky actions not covering keyboard. Test Light/Dark/System, focus ring/contrast, native controls/portals, first paint/reload, long content and PWA update. A PWA cache must never assert current server authority or store business writes offline.

No UI-only hide/show is an authorization check. Server rechecks actor/tenant/permission epoch, source version, policy, and stock at the actual COMMIT; read APIs redact other tenant/customer resources. Export/print would require separate source metadata and permission contracts.

## 7. Review / evidence status

All proposed screen states, layout/keyboard scenarios and source-bound UI→API→SQL results are **NOT_RUN**. C0-R01..04 prevent binding the corresponding transitions; C0-R05/06 and PREPROD gates prevent physical acceptance. No wireframe screenshot or UI-01/02/03 Lab pass can change that. Acceptance matrix: [UI-04 verification](ui-04-verification.md). The [C.5 code PR](../roadmap/app-02c-a-online-e2e.md) must independently demonstrate this contract on a reviewed final HEAD before a UI-04 implementation claim.

