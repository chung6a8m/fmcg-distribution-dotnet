# UI-02 — Form, lookup, table/grid and keyboard behavior

**Implementation target:** `/ui/data-entry` — React/TypeScript UI Lab, **SYNTHETIC_NONPRODUCTION**. Authorization: [direct Owner instruction](../decisions/owner-ui02-2026-11-20.md), separate from UI-01. Uses v0.1 [component contracts](components.md), [interaction contracts](interaction-patterns.md), semantic tokens and UI-01 theme/density controller. This is not a production business screen or API contract.

## Component decisions / bounded ADR UI02-ADR-01

**Decision:** use native HTML form/input and semantic native table with roving-focus `role="grid"` for the small, in-memory fixture; implement a React ARIA combobox/listbox with explicit option identity. No new dependency, full component suite, docking, virtualization, or general-purpose data grid abstraction. Reconsider with a separate ADR if column features, large datasets, remote data or accessibility audits require a maintained grid library.

- `LookupField.tsx`: selected `{id, code, name, detail}` and query are distinct. Typing clears committed ID, filters by normalized Vietnamese code/name/details, never auto-selects. ArrowUp/Down sets active candidate, Enter selects **only active**, Tab exits without selection, Escape restores a previously valid selected candidate. A composing Enter never commits. Pointer selection uses ID, never a display-name match. Clear is explicit. Loading/error/forbidden/empty are announced; source options are synthetic, not tenant-scoped data.
- `decimal.ts`: demo-only positive decimal with dot or comma and at most 3 fractional digits; incomplete buffer stays intact and is rejected only at validation. No grouping separator, implicit 0, automatic cursor-rewriting, finance rounding, or unstated business min/max.
- `EditableGrid.tsx`: semantically labelled four-column table with roving `tabIndex`, arrows in navigation mode, Enter/F2 to open one quantity editor, native caret/arrows while editing, Enter to confirm, Escape to cancel, Tab to confirm and navigate to the next editable cell; last Tab returns to a named external control. Shift+Tab confirms and moves backward; from the first editor it returns to the immediately preceding grid source selector. The horizontal scroll region adds no redundant Tab stop. Validation keeps invalid buffer and exposes linked error. Commit updates **in-memory draft rows only**, never a document or ledger. Selection uses row ID instead of index/label.
- `DataEntryDemo.tsx`: form with duplicate customer names/different IDs, long Vietnamese strings, quantity helper/error, review-only submit, staged filter/sort if a cell is editing, clear selection/visible state and data-source simulation. Stable focus callbacks prevent React rerenders from stealing filter focus during editing. Switching simulated table source state and resetting draft rows are disabled while editor is active (with an explanatory status). Selected ID remains visible as hidden-by-filter rather than silently switching rows.
- `style.css`: UI-01 Light/Dark semantic tokens, compact/comfortable/Field `--control-min` and `--row-min`, native table 36/44 row minimum and editor focus allowance. Narrow/Field layout uses 48px targets and accessible stacked rows, without changing underlying data/roles.

## Keyboard/validation matrix

| Context | Enter / F2 | Tab | Escape | Arrows |
|---|---|---|---|---|
| Lookup query | Enter chooses *active* ID; no form submission | Leaves query, does **not** auto-choose | Closes and restores last valid selection | Moves active option; native caret preserved while composing |
| Form quantity | Never implicitly submits document | Native focus order | Native (no business reversal) | Native caret |
| Grid read cell | Enter selects ID; Enter/F2 on quantity opens editor | Exit to next regular tab stop | No posting/action | Move roving focus row/column |
| Grid number editor | Validate and commit draft buffer | Confirm and move to next row quantity; Shift+Tab goes backward, first exits to source control; last Tab exits after grid | Restore previous buffer value | Native caret; never navigates cells |
| Pending filter/sort | No forced change | Finish/hủy edit first | Hủy edit without discarding row | View stays stable during editing |

Form validation is on blur/check, with input `aria-invalid`/`aria-describedby`, retains user text and focuses the first invalid field. A successful check shows **browser-only** review data; no network command. Error, disabled, source-state, empty, and no-result patterns stay distinct. No business permission is inferred from this fixture.

## Acceptance and limitations

Review against [UI-02 verification matrix](ui-02-verification.md), source tests and Playwright trace/screenshot artifacts. PR #82 completed independent final-source-HEAD review and bounded synthetic 100% browser CI; [post-merge closeout](../closeout/2026-11-20-ui-02.md) records the exact evidence and four resolved findings. This does not replace future physical, source-bound or business acceptance.

Do not claim real data-source cancellation/race ordering, remote permission revocation, high-volume virtualization, screen reader certification, physical Android/iOS/IME/Gboard tests, PREPROD, EV/P0 or E2E business verification. Those require separate authorized increments and evidence.
