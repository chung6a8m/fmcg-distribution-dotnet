# UI-02 verification matrix (synthetic / nonproduction)

**State: `MERGED / UI02_100_PERCENT_CI_VERIFIED` — SYNTHETIC_NONPRODUCTION, browser CI at 100% zoom.** PR #82's reviewed source HEAD and merge tree match; independent technical review and applicable final-source CI were observed. The matrix remains an executable contract; actual evidence is linked below and in the [post-merge UI-02 closeout](../closeout/2026-11-20-ui-02.md). No extension to physical, 125%, PREPROD or production acceptance.

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

## Observed final-source CI and review (20 November 2026)

- [Implementation PR #82](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/82): exact reviewed source `8d3d38309d4f77b7936578839a8a84ce4f2bbd3e`, merge `51388a040ca500743ad8d95591a3e8c56b8d433e`; both have tree `868df53504c1d113f5c832d1de6ec9700b518514`. The source HEAD is the CI identity, not the merge SHA.
- [APP-00A foundation run #44737867771](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44737867771): SUCCESS, SQL/.NET test gate 296/296 (0 failed/skipped), Playwright 38/38, web build and PWA checks. [Playwright artifact #14629492695](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44737867771/artifacts/14629492695); [test evidence #14629203050](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44737867771/artifacts/14629203050).
- [Bounded web regression run #44737867746](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44737867746): SUCCESS, Playwright 38/38, parser/theme/TypeScript/Vite/PWA checks. [Artifact #14629876220](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44737867746/artifacts/14629876220). Applicable final-source GitHub Actions: **10/10 SUCCESS**; check-runs **20/20 SUCCESS**.
- Four independent-review findings UI02-RV01..04 (1 P1, 3 P2) were fixed in commit `8d3d383` with browser regressions; [review closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/82#pullrequestreview-5992931445). No blocking findings or unresolved threads at merge. Full findings matrix: [closeout](../closeout/2026-11-20-ui-02.md).

These are CI-observed browser and source checks, **not** end-user physical IME/assistive-tech/device verification. In particular, UI-02's own 125% browser zoom is `NOT_TESTED`; APP-00B/C's historical exception is not extended.

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
| UI02-G04 | Per-keystroke filter typing while editing retains focus; reset disabled during editing with reason; single grid Tab stop; first-row Shift+Tab exits backward |
| UI02-V01 | 1440 desktop Compact and 320 Field; control/editor min sizing, page overflow, long Vietnamese text, Light/Dark tokens |
| UI02-R01 | No regression of UI-01 theme, first paint, native dialog, PWA consent/update and existing suite |

Test implementation: [unit parser](../../web/tests/ui-02.test.mjs) and [browser](../../web/e2e/ui-data-entry.spec.ts). Browser tests attach geometry JSON and dark screenshots. Use Playwright trace/evidence from the **same CI SHA** when evaluating behavior.

## Evidence boundaries

- Source PR and CI validation demonstrate synthetic browser/DOM behavior only; they cannot show correctness against an authorized A_ONLINE command.
- Local form and grid data are deliberately not stored through reload. PWA remains online-first; no offline write queue.
- Physical devices, installed-app launcher, real IME/scanner/accessibility-assistive tech and end-user testing are **NOT_RUN** unless separately observed.
- 125% APP-00B/C remains its historical `EXCEPTION_ACCEPTED / NOT_TESTED` scope, not an automatic UI-02 compliance waiver. Native UI-02 125% browser zoom acceptance is `NOT_TESTED`; the 100%-scale Compact/Field geometry checks cannot substitute for it.
