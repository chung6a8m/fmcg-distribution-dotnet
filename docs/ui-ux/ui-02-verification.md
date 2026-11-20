# UI-02 verification matrix (synthetic / nonproduction)

**State:** TESTS_ADDED / FINAL_HEAD_CI_PENDING. Independent PR technical review and CI results must be recorded against the **final source HEAD** before any `CI_VERIFIED` claim. This document describes executable checks, not evidence already observed.

## Commands (Node 26.13.1, lockfile-pinned web dependencies)

From repository root:

`node docs/ui-ux/tools/sync-tokens.mjs --check`
`node docs/ui-ux/tools/check-tailwind.mjs`

From `web/` after `npm ci`:

`npm run build`
`npm test`
`npm run test:pwa-build`
`npx playwright test e2e/ui-data-entry.spec.ts`
`npx playwright test e2e/ui-foundations.spec.ts e2e/theme.spec.ts`

Existing workflows additionally run broader .NET/SQL, PWA/security/tenant regressions; record actual GitHub workflow and check-run identities, no substitutions.

## Browser and unit cases

| Gate | Observable assertion |
|---|---|
| UI02-F01 | Lab-only route from health shell; no business fetch/XHR while interacting |
| UI02-F02 | Form submit invalid shows field-linked errors and focuses first issue; retains buffer and separate review result |
| UI02-L01 | Duplicate lookup display names remain distinct by ID/code; no implicit selection from text |
| UI02-L02 | IME composing Enter ignored; arrows active option; Enter explicit; Tab no auto-commit; Escape restores last valid identity |
| UI02-L03 | First character, caret, Vietnamese search, long label, no result and loading/error/forbidden states |
| UI02-N01 | Parser accepts comma/point and precision fixture; rejects incomplete/grouped/nonfinite/zero; no implicit 0 |
| UI02-G01 | Roving grid arrows; Enter/F2 enter editor; editor arrows adjust caret; Escape cancels, Enter commits local draft |
| UI02-G02 | Invalid editor buffer stays visible with accessible error; Tab advances valid draft; last Tab exits grid |
| UI02-G03 | Selection keyed by row identity; filter/sort staged until editor ends; hidden selected ID disclosed |
| UI02-V01 | 1440 desktop Compact and 320 Field; control/editor min sizing, page overflow, long Vietnamese text, Light/Dark tokens |
| UI02-R01 | No regression of UI-01 theme, first paint, native dialog, PWA consent/update and existing suite |

Test implementation: [unit parser](../../web/tests/ui-02.test.mjs) and [browser](../../web/e2e/ui-data-entry.spec.ts). Browser tests attach geometry JSON and dark screenshots. Use Playwright trace/evidence from the **same CI SHA** when evaluating behavior.

## Evidence boundaries

- Source PR and CI validation demonstrate synthetic browser/DOM behavior only; they cannot show correctness against an authorized A_ONLINE command.
- Local form and grid data are deliberately not stored through reload. PWA remains online-first; no offline write queue.
- Physical devices, installed-app launcher, real IME/scanner/accessibility-assistive tech and end-user testing are **NOT_RUN** unless separately observed.
- 125% APP-00B/C remains its historical `EXCEPTION_ACCEPTED / NOT_TESTED` scope, not an automatic UI-02 compliance waiver.
