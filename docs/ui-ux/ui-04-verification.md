# UI-04 — Sales A vertical-slice verification plan

**Status: MATRIX_SPECIFIED / 0 UI04 CASES RUN / IMPLEMENTATION_BLOCKED.** Documentation-only checklist. Does not add tests, run browser/API/SQL cases, freeze C expected fixtures or grant C-DEV, C-PREPROD or C-E2E admission.

[Readiness/ADR](ui-04-readiness.md) · [screen spec](ui-04-sales-a-screen.md) · [APP-02C.0 contract](../roadmap/app-02c0-a-online-e2e-contract.md) · [independent C roadmap](../roadmap/app-02c-a-online-e2e.md). Case IDs here are **proposed UI verification identifiers**, not approved C.6 oracle case counts.

## Entry gates

| Gate | Requirement | Status 20/11/2026 |
|---|---|---|
| UI04-G00 documentation | Reviewed screen/ADR/verification mapping, source contracts, exact docs PR final-HEAD CI | PENDING REVIEW |
| UI04-G01 source authority | APP-02B foundation or an explicit narrowly scoped pre-B exception, new C-DEV decision naming increments, applicable C0-R01..06 closure | BLOCKED |
| UI04-G02 implementation | Authoritative C.1→C.4 sources available, separate C.5 authorization/PR, no invented API/permission/policy | NOT_RUN |
| UI04-G03 browser/API/SQL CI | final-source PR review, all applicable workflows/Playwright/SQL/negative/recovery tests | NOT_RUN |
| UI04-G04 physical E2E | C-PREPROD, two physical tenant DBs, independently frozen expected/actual/difference, C.6 review | NOT_RUN / BLOCKED |

## Candidate executable verification matrix

| ID | Scenario and independently observable acceptance |
|---|---|
| UI04-01 | Sales workspace only appears for a current authorized resource/tenant; direct/deep-link reload denies unauthorized Customer/Order |
| UI04-02 | Customer debtor distinct from optional Outlet; registered/direct ShipTo validated at server with snapshot provenance and amendment boundary |
| UI04-03 | Identity lookup survives typing, IME/caret, arrows, Enter/Tab; no commit on mere text or double tab |
| UI04-04 | Desktop Compact 1440: 14/13px, min control/row, dense but readable; long Vietnamese/errors expand, focus intact |
| UI04-05 | Light/Dark/System including opposite-OS preference, native/portal, PWA reload/update; no first-paint theme flash |
| UI04-06 | Narrow 320 CSS px/Field intent and safe area/keyboard handling; physical device acceptance separately scoped, not inferred |
| UI04-07 | List totals always show scope/asOf/completeness; complete zero, missing data, partial sources, unknown denominator and stale remain distinct |
| UI04-08 | Confirm snapshots approved policy/ShipTo/terms at source, creates no sale/COGS/AR; missing/UNSET/currently invalid policy denied |
| UI04-09 | Confirm success + Reserve failure exposes CONFIRMED_UNRESERVED; no Load; explicit same-order Reserve recovery without re-Confirm |
| UI04-10 | Reserve checks tenant/current epoch, stock right/B quota and competition; cannot reserve duplicate or over-available stock |
| UI04-11 | Load atomic MAIN→TRIP tied to reservation, no AR/COGS; loaded and un-loaded remainder separately reconciled |
| UI04-12 | Partial/repeated/competing Load, replays, conflict/rollback and authorized release/unload retain ledger conservation |
| UI04-13 | One partial PostDelivery consumes only actual delivered right/TRIP stock, posts correct COGS/AR once; remainder HELD |
| UI04-14 | Due date uses approved seller-local business date + frozen Confirm terms in calendar days; master/policy change does not rewrite history |
| UI04-15 | Command preflight is not server authority; one explicit action per command; immutable payload/ID and current resource recheck at COMMIT |
| UI04-16 | Lost ACK, PENDING/UNKNOWN and durable intake ACK: same-ID outcome lookup, no implicit new-ID replay or fabricated POSTED |
| UI04-17 | Read/write revocation, wrong tenant, stale epoch, unauthorized resource: no source exposure, no submission or cached cross-tenant leakage |
| UI04-18 | Offline before command disables posting and has no service-worker queue; refresh/reconnect requires new server authorization |
| UI04-19 | AR query source + asOf + allocation/correction/reconciliation, no duplicate client ledger or zero-for-missing |
| UI04-20 | External/independent SQL oracle: approved frozen expected, two physical tenant DBs, fault/restart/lost ACK, exact commit/HTTP/SQL/OTel source and negative witnesses |

**Status for every UI04-01..20: NOT_RUN.** UI04-02/08..14/19 depend on C0-R01..04 and C.1..C.4; UI04-20 additionally requires C0-R05/R06 plus C-PREPROD and C.6. UI04-04..07, even if partially demonstrated by UI-01/02/03, must be **retested on the actual UI-04 code**; previous synthetic evidence does not transfer.

## Evidence protocol for future code PR

Record scope, authorized actors/tenant binding and permissions, source HEAD, code diff, workflow/job/artifact IDs, pass/fail/skip, UI accessibility and keyboard/IME/browser traces, SQL actual/expected signed difference, test data provenance, peer-tenant negative, fault/restart, independent reviewer and Owner disposition. Correlate request ID/payload hash across UI→HTTP→source/ledger/AR without exposing secrets or PII. Never accept a single HTTP 200, localStorage/sessionStorage mock, screenshot or UI-03 synthetic ACK as committed business evidence.

Required regressions: UI-01 themes/foundations, UI-02 grid/lookup, UI-03 UNKNOWN/permission, health/PWA, APP-00A .NET/LocalDB, APP-01E, APP-02A and applicable B/tenant/security gates on **exact final reviewed source HEAD**, with failures/skips disclosed. APP-00B/C's historical 125% exception is not a blanket UI-04 acceptance.

For **this docs-only PR**, only check no edits to `business/`, `business.lock.json`, runtime, migrations, package locks and workflows; relative links, status consistency, separate authority mapping, independent review and final-source CI. Do not promote DOCS_CI to implementation/physical PASS.

