# UI-01 Runtime Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply adopted UI/UX v0.1 to the actual web shell and a synthetic foundations demo, with source-bound runtime evidence in one independent PR.

**Architecture:** One pre-paint classic theme controller is shared with React through a typed external-store adapter. Native semantic controls consume canonical tokens and self-hosted Noto Sans; production-build browser tests exercise the real UI/PWA under the existing CI gates.

**Tech Stack:** React 19.3.1, TypeScript 7.0.2, Vite 8.3.8, Tailwind 4.3.5, Playwright 1.65.1, Node 26.13.1+. No new frontend dependency.

**Spec:** [UI-01 runtime specification](../../ui-ux/ui-01-runtime-foundations.md); [OWN-UX-01..09](../../decisions/owner-uiux-2026-11-20.md).

## Global Constraints

- UI-01 only: foundations/demo controls, no UI-02..05, business/API writes, grid/lookup suite, tenant override or offline commands.
- Keep `business/`, `business.lock.json`, backend code, dependency versions/lockfile and current security/SQL gates intact.
- Noto Sans 400/700 self-hosted; compact body14/20, table13/18, control32/row36; comfortable40/44; Field16/24 and target48×48 from320 CSS px.
- Light/Dark/System; missing/invalid/unreadable preference resolves Light; explicit choice wins OS; write failure never undoes the in-session choice.
- Keep source HEAD separate from CI synthetic merge SHA. Preserve 125% APP-00B/C EXCEPTION_ACCEPTED / NOT_TESTED; do not promote browser evidence to physical-device/PREPROD acceptance.

## Review Focus

- Storage throwing with an older contrary saved preference must not reset a selected theme after StrictMode resubscription — Task 1 unit + Task 3 browser.
- Parser/bootstrap timing must set actual Dark/root/native colors before React executes under CSP — Task 3 delayed-entry production test.
- Noto font failure, combining Vietnamese marks and multiline invalid text must not clip or overflow at320px — Tasks 2/3 glyph and fallback tests.
- Dialog/density/theme changes must preserve keyboard focus and input caret; touch targets must not overlap adjacent rows — Tasks 2/3 interaction/geometry tests.
- A cached shell and user-confirmed SW update must preserve theme and load fonts while API remains uncached/disconnected offline — Task 3 real SW test.

---

### Task 1: Shared pre-paint theme controller

**Files:** create `web/public/theme-runtime.js`, `web/src/ui/theme.ts`, `web/tests/theme.test.mjs`; modify `web/index.html`, `web/vite.config.ts`, `web/package.json`.

**Interfaces:** `window.FmcgTheme.getSnapshot(): Readonly<{preference:'light'|'dark'|'system',resolved:'light'|'dark',persistent:boolean}>`; `setPreference(preference):void`; `subscribe(listener):()=>void`. React exports `useTheme()` and `setThemePreference`. Key `fmcg.ui.theme.v1`. Theme-color meta has generated `data-light`/`data-dark` from canonical page-surface tokens; runtime contains no palette literals.

- [ ] Write Node VM tests that execute the public controller: Light on Dark OS, Dark on Light OS, System OS changes, invalid/missing/read-blocked values, media absent/throwing, write failure with older contrary storage, stable snapshot identity, same-resolved preference changes, single listener/multiple subscribers, cleanup/resubscribe.
- [ ] Run tests before implementation and record the expected missing-feature failure.
- [ ] Implement the controller, typed external-store adapter and head script; Vite injects palette metadata from token JSON. Preserve title/manifest app identity and existing PWA setup. Include theme tests explicitly in `npm test` alongside health tests.
- [ ] Run `npm test`, `npm run build`, `npm run test:pwa-build`, review the task diff and commit after tests pass.

### Task 2: Actual foundations and demo controls

**Files:** create `web/src/ui/controls.tsx`, `web/src/ui/FoundationsDemo.tsx`, `web/public/fonts/{noto-sans-400.woff2,noto-sans-700.woff2,OFL.txt,README.md}`, `web/tools/build-fonts.py`, `web/e2e/ui-foundations.spec.ts`; modify `web/src/main.tsx`, `web/src/style.css`, `web/vite.config.ts` and optional local-browser override in `web/playwright.config.ts`.

**Interfaces:** consume `useTheme`/`setThemePreference` from Task1; `FoundationsDemo` owns local density and demonstration values. Export only small native wrappers used by the shell/demo. Theme selector label `Chế độ hiển thị`; demo heading `Nền tảng giao diện`; route `/ui/foundations`; health remains `/` with original APP-00A identity.

- [ ] Add browser acceptance for a real demo entry, root theme changes, native input states, portal focus/Escape, compact/table/Field geometry and long Vietnamese text; observe initial failure against the existing shell.
- [ ] Build deterministic static font subsets from the existing TTF, preserving needed Vietnamese/numeric glyphs; include license/provenance and font-display swap. Copy original copyright, no CDN/dependency updates.
- [ ] Import token CSS/adapter; implement semantic Light/Dark styles and the actual demo. Include a native modal portal, local pending action, readonly/disabled/invalid states, long-name sample rows and Field layout. Preserve existing health/SW behavior.
- [ ] Run build, all Node/PWA tests and browser suite; inspect desktop Light/Dark and narrow screenshots. Correct actual visual/interaction failures; record font/geometry evidence and review the task before committing.

### Task 3: Runtime acceptance, regression and handoff

**Files:** create `web/e2e/theme.spec.ts` and test helpers as needed; extend `web/e2e/pwa-update.spec.ts`, `web/tests/pwa-build.test.mjs`, package scripts; update `README.md`, UI/UX README/roadmap/theme/governance/reference comments and the UI-01 specification with implemented scope and evidence links. No new workflow needed unless existing scripts fail to execute an applicable gate.

**Interfaces:** tests consume the production build, stable accessible names and the Task1 controller; generated evidence goes to the existing ignored `web/test-results/` and CI artifacts.

- [ ] Write delayed-entry strict-CSP browser tests; exercise storage errors, explicit/System switching, actual root/native/portal colors, both font weights and fallback; add literal expected contrast thresholds and geometry.
- [ ] Extend real PWA V1→V2 consent test with retained Dark, bounded keyboard navigation to the update action and font/cache assertions; add an offline controlled reload and feasible standalone evidence without mocking display-mode as acceptance.
- [ ] Run token parity/compiler in an executed package script and extend generated PWA tests to require bootstrap/fonts. Preserve all original health and update assertions.
- [ ] Run the complete frontend verification under Node26, collect screenshots/measurements, and record local browser limits separately from CI. Independent whole-branch review; fix findings and repeat only affected verification plus required gates.
- [ ] Open the independent UI-01 PR, verify all applicable CI workflows on the final source HEAD, and provide evidence/handoff. Keep unobserved physical-device and installed OS-launch scopes explicit.
