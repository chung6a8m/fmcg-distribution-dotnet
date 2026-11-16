# Issue #13 PWA External Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining APP-00B/C browser gate with a real-browser-safe PWA update action, reproducible evidence, and explicit Owner acceptance.

**Architecture:** Keep `vite-plugin-pwa` for registration and refresh notification, but make the user action independent of Workbox's internal classification of an update as local or external. Store the registered `ServiceWorkerRegistration`; when the prompt is visible, post `SKIP_WAITING` to its actual waiting worker and reload only after `controllerchange`, retaining the plugin callback only as a no-registration fallback.

**Tech Stack:** React 19, TypeScript 7, Vite 8, vite-plugin-pwa 2, Workbox, Playwright 1.65, Node 24.22.2.

**Spec:** GitHub Issue #13 and `docs/roadmap/app-00b-dependency-modernization.md`.

## Global Constraints

- Do not modify `business/` or `business.lock.json`.
- Use Node 24; do not claim a Node 26 run as APP-00B evidence.
- Keep `registerType: 'prompt'`; no silent reload and no offline business writes.
- 125% zoom/scaling remains `EXCEPTION_ACCEPTED / NOT_TESTED`, never PASS.
- Evidence is synthetic/nonproduction only and does not authorize EV/P0 or tenant go-live.

## Review Focus

- An update discovered by a second open client must still let the first prompted client activate and reload.
- The prompted client must remain on V1 until the user clicks.
- Keyboard focus must remain visible on the update button.
- A missing/stale waiting worker must not throw; the existing plugin updater remains the fallback.
- `controllerchange` must cause at most one explicit application reload from the app handler.

---

### Task 1: Make user-confirmed activation robust for external updates

**Files:**
- Modify: `web/e2e/pwa-update.spec.ts`
- Modify: `web/src/main.tsx`
- Create: `docs/evidence/issue-13/2026-11-16/README.md`
- Create: `docs/evidence/issue-13/2026-11-16/*.png`

**Interfaces:**
- Consumes: `registerSW(options)` returning the existing plugin update callback and `onRegisteredSW(swScriptUrl, registration)`.
- Produces: a button handler that targets `registration.waiting`, posts `{ type: 'SKIP_WAITING' }`, and reloads once after `navigator.serviceWorker` emits `controllerchange`.

- [x] **Step 1: Write the browser characterization**

Change the PWA test's update trigger to open a second page on the same origin after switching the test server from V1 to V2. Assert that page 1 shows the prompt, keeps its V1 marker before consent, and reaches the V2 title after clicking.

- [x] **Step 2: Run the focused test and evaluate the result**

Run: `npm run test:e2e -- e2e/pwa-update.spec.ts`

Planned expectation was a final V2-title failure. Actual result on unchanged production code: `1/1 PASS`; the suspected application defect was not reproduced.

- [x] **Step 3: Apply the TDD ruling**

No production change was made. TDD forbids introducing a speculative fix after the external-client characterization passed before implementation; the retained same-tab test remains the issue's authoritative regression.

- [x] **Step 4: Verify the complete frontend suite**

Run on Node 24.22.2:

`npm run build`

`npm test`

`npm run test:dependencies`

`npm run test:pwa-build`

`npm run test:e2e`

Expected: all commands exit 0; the PWA regression proves prompt, no silent reload, focus, click, and V2 reload.

- [ ] **Step 5: Record and cross-link acceptance evidence**

Document commit, OS, Edge version, Node/npm versions, 100% desktop/narrow metrics, console health, PWA steps, screenshots, the 125% exception, and the synthetic/nonproduction boundary. Link the evidence from Issue #13 and PR #5, then request explicit Owner acceptance before closing #13.

- [x] **Step 6: Commit**

Commit only the focused source, test, and evidence files with a small issue-13 commit; do not include generated `dist`, reports, `node_modules`, or `tsconfig.tsbuildinfo`.
