# UI-03 — Source, command and recovery UX (synthetic UI Lab)

**Surface:** /ui/command-recovery. **Authorization:** [direct Owner instruction](../decisions/owner-ui03-2026-11-20.md), independent PR. Uses [UX-B01…B07](business-patterns.md), [interaction patterns](interaction-patterns.md) and the adopted tokens. No business API integration or backend outcome guarantee.

## Bounded ADR UI03-ADR-01

Reuse React 19, native semantic form/select/button, definition lists, UI-01 Badge/Notice/ThemeSelect and a pure TypeScript state reducer. No additional library, service worker override, new API, WebSocket, invented SLA, policy default, offline write or generic command bus. The reducer is a **UX fixture only**, not a production server protocol. A production adapter must obtain all transitions and authority from authenticated, resource-scoped API responses.

## Source presentation — UX-B01/B02

| Fixture | Presentation |
|---|---|
| Partial | Two reports of three expected, one real reported value zero; sum explicitly **known portion** and list missing source |
| Complete-zero | Two of two known reports and confirmed total **0 ₫** |
| Denominator unknown | Two known reports; total count undetermined, never a fabricated 2/0 |
| Read failure / stale | Keep the source's 19 November asOf while showing a failed-refresh notice |
| Empty | No report exists in selected scope; **no data**, not a confirmed financial zero |

Detail disclosure shows known report IDs and missing IDs. The no-read fixture hides numerical values and report list. All numbers, times, counts and scope labels are samples, not live seller data or global policies. The source asOf is never replaced by the device time.

## Command workflow — UX-B03…B07

1. Draft: quantity/version editable. Preflight checks only positive decimal buffer, simulated assignment and connectivity. Changing any of these invalidates the previous preflight.
2. Checked: one explicit synthetic send is allowed; server must independently recheck authority, version, stock and policies at commit.
3. Pending: ID, quantity and revision freeze; duplicate send is rejected at reducer level. No transport ACK, business posting or ledger effect is implied.
4. UNKNOWN: lost ACK or lookup with no result is not rollback/rejection. The only continuation is **Kiểm tra kết quả lần gửi này**, referencing the same ID. No alternate ID, replay or offline queue is offered.
5. ACK/waiting: intake ACK is *not* POSTED; waiting on dependency is *not* completed. A subsequent empty lookup must not erase a previously established ACK/dependency state.
6. POSTED/rejected/conflict: results appear only after selecting the corresponding fixture outcome. POSTED does not establish accounting or reconciliation; rejected-before-effect may start a separate toy draft; conflict never offers a new-ID bypass.
7. Read/write/none and online/offline are explicitly **browser-side simulations**. No-read hides snapshot and history, prevents lookup; read-only permits lookup but not submit; offline prevents business send/lookup and never enqueues.
8. Results are rendered by distinct layers: receipt ACK, business outcome, invoice/accounting/reconciliation, post-command balance. The last two are never inferred from a transport response; ambiguous post-balance remains unknown.

The current request and local history are versioned in **sessionStorage, same tab only**, with bounded schema validation and storage-failure fallback. This is not a durable inbox, real audit, trusted identity/permission or recovered business truth. Clearing/denying storage can lose the fixture; an untrusted client copy cannot be used as server evidence. Statements about physical handover or delivery requiring a case are guidance text only: no case is created.

## Verification and limitations

See [UI-03 verification](ui-03-verification.md). Tests cover source semantics, stable request ID, state ordering, loss of ACK, permission/offline, recovery on reload, 1440/320 Dark and compact/Field, plus UI-01/02/PWA regressions. No physical IME/Gboard, 125% Lab acceptance, server authorization, actual source/ledger or PREPROD evidence is asserted. Real source-bound reconciliation and recovery need separately authorized backend/API work.
