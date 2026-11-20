# UI-01 runtime verification

Design v0.1 is `ADOPTED` via merged PR #79. UI-01 is `MERGED / CI_VERIFIED` within synthetic/nonproduction foundations only; [post-merge closeout](../closeout/2026-11-20-ui-01.md). Implementation [PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80) merged as `a0081b081221de725279e1d81b7d3dd8f4f93488`, retaining reviewed source HEAD `2575651ae18e2e765b8072ef0b82409afce3319e` and identical source tree `88097b1731fc564adbefed3b19c12bcd66376ee6`. The source HEAD (not the merge commit) has 10/10 successful workflows and 20/20 successful check runs. Use the PR body and Checks tab for the source-specific workflow run/attempt IDs, conclusions and artifact links. Match the pull-request event's `pull_request.head.sha` to the reviewed source. `GITHUB_SHA` can be the workflow's synthetic merge. Artifacts from earlier revisions describe those revisions only. [UI-01 PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80) · [PR checks](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80/checks).

## Retained exact-source CI evidence — 20 November 2026

| Gate | Observed result and retained evidence |
|---|---|
| APP-00A .NET/SQL + web | [Run 44709397882](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397882): 296/296 .NET tests, 28/28 Playwright; [browser artifact 14616154261](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397882/artifacts/14616154261) and [SQL/.NET artifact 14616477823](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397882/artifacts/14616477823). |
| Bounded web/PWA regression | [Run 44709397720](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397720): 28/28 Playwright; [browser artifact 14616208371](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397720/artifacts/14616208371). |
| Security and other CI jobs | [Issue #59 security run 44709397853](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44709397853) SUCCESS; additional APP-02B.2/.3/.4/.5/.6/.7/.8 source/CI workflows SUCCESS. 10/10 applicable workflows, 20/20 check runs SUCCESS on `2575651ae18e2e765b8072ef0b82409afce3319e`. This does not grant Human Security signoff or physical PREPROD. |

The merge tree matches the previously reviewed source tree; CI was executed on the PR's final **source HEAD**, not independently on the merge commit. Source-bound browser artifact evidence is limited to synthetic Windows Chromium and the documented nonproduction scope.

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
