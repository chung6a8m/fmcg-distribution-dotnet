# UI-01 runtime verification

Design v0.1 is `ADOPTED` via merged PR #79. UI-01 is `IMPLEMENTED_IN_PR`. Use the PR body and Checks tab for the final source HEAD, workflow run/attempt IDs, conclusions and artifact links. Match the pull-request event's `pull_request.head.sha` to the reviewed source. `GITHUB_SHA` can be the workflow's synthetic merge. Artifacts from earlier revisions describe those revisions only. [UI-01 PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80) · [PR checks](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80/checks).

## Run and interpret

Use Node 26.13.1+ within 26.x and the checked-in lockfile. Run the frontend commands from `web/`. The two existing CI jobs invoke the theme, token/metadata and compiler checks explicitly, preserving the Issue 59 package, lockfile and Vite glob boundaries.

| Check | Command / evidence |
|---|---|
| Health contract and theme controller | `npm test` and `node --test tests/theme.test.mjs`. The latter executes the actual classic bootstrap in an isolated VM. |
| Dependencies | `npm run test:dependencies` |
| Token/HTML/manifest parity and declared contrast pairs | `node ../docs/ui-ux/tools/sync-tokens.mjs --check` |
| Tailwind compiler | `node ../docs/ui-ux/tools/check-tailwind.mjs` |
| Production bundle and generated worker/manifest | `npm run build` then `npm run test:pwa-build` |
| Discover browser tests | `npx playwright test --list` |
| Execute browser tests | CI installs pinned Chromium and runs `npm run test:e2e`. The installed runtime case requires a disposable Windows CI runner. |

| Browser scope | Observable result under test |
|---|---|
| Health and navigation | Exact APP-00A shell identity, validated API live/disconnected status and links to the synthetic foundations demo. |
| Before React | Actual build with same-origin scripts/styles and `font-src 'self' data:` for embedded WOFF2. Hold the module entry, inspect the empty root, CSS surface, native scheme and metadata after frame callbacks. Six explicit/System/absent/invalid/blocked cases. A separate inline script probe must be refused. |
| Theme controls and keyboard | OS changes in System, explicit overrides, persistence, write failure with contrary stored value, new dialog subscriber, native modality and focus return, stable editor node/value/selection through density/theme changes. |
| Font and contrast | Chromium platform font family, PostScript name and glyph counts on Vietnamese 400/700, normalized combining marks and currency samples; external CSS embeds the exact two source assets; a separate `font-src 'none'` fixture forces fallback at 320px. Rendered text, control borders and focus contrast in Light/Dark with disabled controls labeled separately. |
| Layout/accessibility | 1440×900, 1024×768, 430×932 and 320×800 at 100%; existing 1280/375 health checks. Compact/Comfortable/Field metrics, long Vietnamese text, native field semantics, forced colors and reduced motion. |
| Real service worker | Activated and controlling V1; V2 waits for keyboard consent before reload. Controlled offline reload retains Dark and custom glyphs, reports API disconnected and has no cached health/business/API data. |
| Installed standalone | Full Chromium, native pipe, isolated persistent profile. CDP install, standalone preference, matching app tab/page/window, native display mode, Dark, then Light across reload and relaunch. Uninstall and remove the profile. Installation bypasses the end-user dialog. |

Base64 encoding the two WOFF2 payloads adds 131,344 characters to the external render-blocking stylesheet. The delayed-entry experiment measures theme ordering; cold-load latency needs separate timing evidence.

The two Windows workflows retain Playwright HTML report, JUnit, screenshots, traces on failure and JSON attachments. Download `app00b-playwright-evidence` (APP-00A) or `app02b4-bounded-web-regressions-ci-only` (bounded frontend job) from the run identified by PR #80. Inspect the specific failing or successful test, retry and source metadata. [APP-00A workflow](../../.github/workflows/app00a.yml) · [bounded web workflow](../../.github/workflows/app02b4-web-regressions-with-known-debt.yml).

## Boundaries

Local automated browser execution is `NOT_RUN_LOCAL_ENVIRONMENT`: Chromium socket creation was blocked. The connected Browser served a different asset revision; current-source manual QA there is `NOT_RUN`. CI is the source of build-bound browser evidence. A missing PWA protocol capability on the intended CI browser is an unsuccessful `BLOCKED_CAPABILITY` result and requires investigation.

Visible OS launcher, shortcut, splash/chrome, end-user installation UX, Android/iOS hardware, software keyboards and physical scanner workflows remain unverified. The 125% APP-00B/C exception remains `EXCEPTION_ACCEPTED / NOT_TESTED` in its original scope. UI-02..05 remain `NOT_AUTHORIZED`; synthetic UI-01 results confer no business, production or accessibility certification. [125% Owner decision](../decisions/owner-app00b-13-02-125-scale-exception.md).
