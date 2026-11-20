# UI-01 — Runtime Foundations

**Status.** Design v0.1 is `ADOPTED` via [PR #79](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/79). UI-01 under OWN-UX-09 is `IMPLEMENTED_IN_PR` in [PR #80](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/80). The [verification guide](ui-01-verification.md) locates source-bound results. UI-02..05 remain `NOT_AUTHORIZED`.

## 1. Purpose, users and entry points

The existing `/` health/PWA shell remains operational. `/ui/foundations` is a clearly labeled, synthetic demonstration of actual reusable controls, typography, density and theme. Native links connect the two pages; a pathname switch is sufficient for these two read-only entry points. No router, grid, icon or component-suite dependency is added.

The audience is the project implementer/reviewer preparing later screens. The demo only owns local presentation state. It does not load or write business data, change tenant scope, or implement UI-02..05. APP-00A identity markers and its health and consent-driven PWA update contracts remain intact.

## 2. Shared theme and first paint

- `web/public/theme-runtime.js` is one classic, parser-blocking, same-origin script in the HTML head. It contains the only preference validator/resolver and storage/media lifecycle. React uses the same controller through a typed `useSyncExternalStore` adapter in `web/src/ui/theme.ts`.
- Preference key: `fmcg.ui.theme.v1`. Valid values: `light`, `dark`, `system`. Missing, invalid or unreadable storage defaults to Light. Explicit Light/Dark wins over OS. Missing/throwing media API resolves System to Light.
- `window.FmcgTheme` exposes `getSnapshot()`, `setPreference(preference)` and `subscribe(listener)` returning cleanup. Snapshot is cached and immutable: `{ preference, resolved, persistent }`. `persistent` means the current preference is known to be saved: valid initial storage or successful write; it does not promise future writes. A failed write preserves the in-document choice and marks it unsaved.
- The root has only `data-theme="light"` or `data-theme="dark"`. OS listeners exist only while System is selected and a subscriber exists; the first subscription resamples OS and final unsubscription removes the listener. React remount never rereads old storage. Cross-tab synchronization is outside UI-01.
- Root CSS supplies native `color-scheme`; bootstrap also sets a matching color-scheme meta before CSS. A single theme-color meta follows the resolved palette. `docs/ui-ux/tools/sync-tokens.mjs --write` generates the checked-in head metadata and static manifest color literals from canonical tokens; `--check` detects drift; no duplicated hardcoded palette is maintained in the controller. Manifest launch colors use the Light surface as an explicit static fallback.
- No inline script/style, unsafe-inline, eval, global security relaxation or production CSP deployment is introduced. Production-build acceptance serves enforcing CSP with same-origin scripts and styles. Only `font-src` permits data URLs to decode packaged WOFF2s. A separate `font-src 'none'` fixture exercises fallback. The test holds the React entry to inspect the styled root before mount; it does not measure cold-load latency. Development HMR remains separate.

## 3. Fonts, tokens and density

Import `docs/ui-ux/tokens/tokens.css` in the theme layer and its Tailwind v4 adapter from the web entry CSS. Keep token values and provenance unchanged; active documentation describes runtime consumption separately from historical reference evidence.

Two static WOFF2 assets (400/700, normal width) under `web/src/assets/fonts/` derive from the checked-in Noto Sans 2.015 source. See `web/public/fonts/` for OFL and reproducible provenance. Relative CSS `?inline` references embed their exact bytes in the external same-origin stylesheet. The two base64 payloads add 131,344 characters before compression. CSS and font updates share a cache lifetime. Retain Latin/Vietnamese base and combining characters, punctuation, currency and numeric symbols; preserve tabular numerals. Use `font-display: swap` and the approved Segoe UI/system fallback. No CDN or synthetic 500/600 weight.

| Mode | Body | Table | Control floor | Reading row floor |
|---|---|---|---|---|
| Compact default | 14/20px | 13/18px | 32px | 36px |
| Comfortable demo | 14/20px | 13/18px | 40px | 44px |
| Field demo / narrow touch layout | 16/24px | cards or label/value | 48×48px targets | content-driven |

Density choice is local to the demo and does not modify theme, data or permissions. Native table rows use height as a floor, not a fixed clipped block. Editor cells reserve the reference focus clearance; long Vietnamese names and validation messages increase height. At narrow viewports, layout becomes one column and controls retain Field targets. No fixed row-count quota.

## 4. Component anatomy and state ownership

Small semantic primitives live in `web/src/ui/`: Button, Field/Input, Badge/Notice, ThemeSelect, and a native dialog rendered through `createPortal(..., document.body)`. Demo composition is separate from controller and primitive code.

| State/interaction | Demo behavior |
|---|---|
| Default, hover, pressed, selected | Semantic palette; selection includes text/state, not color alone |
| Disabled | Native disabled prevents activation; visible adjacent reason |
| Read-only | Readable, focusable and selectable value; edits do not change it |
| Invalid | Visible error connected with aria-describedby and aria-invalid; text wraps |
| Pending | Local demo action changes pending/result state and prevents duplicate starts; no HTTP/business write |
| Dialog | Accessible name; initial focus, native modal containment/inert background, Escape and focus return; inherits root theme outside React root |
| Loading/empty/error samples | Clearly labeled synthetic states, never presented as posted business results |
| UNKNOWN/conflict/permission/physical confirmation | Not simulated as functioning business features; their source contracts remain in UI-03/later increments |

Keyboard: native Tab/Shift+Tab, Enter/Space activation, native select arrows and dialog Escape. Field input retains IME typing and caret through presentation changes. The simple table is a typography/geometry sample, not a data-grid editing or lookup contract. Native focus uses approved 2px outline/2px offset with adequate clearance. Forced colors keep system focus/borders; reduced motion removes nonessential motion.

## 5. PWA and data boundaries

Keep prompt-based service-worker updates, user-confirmed activation, empty runtimeCaching and existing API navigation exclusion. Precache bootstrap, CSS (including both font payloads), JS and HTML so a controlled cached reload has the same foundation. Missing API while offline remains disconnected; there is no offline business command queue.

Tests cover cold start, controlled reload, cached/offline shell and real V1→V2 service-worker consent with retained theme. Actual standalone windows are verified where the browser supports isolated installation/launch. OS launch-screen behavior and physical Android/iOS/virtual-keyboard/scanner acceptance are reported separately; viewport emulation never substitutes for those observations.

## 6. Acceptance and evidence

Use Node 26.13.1+, locked Tailwind 4.3.5 and the existing Playwright harness. CI uses its pinned Chromium; any different local browser/version is recorded explicitly. Viewports: 1440×900, 1024×768, 430×932, 320×800 at 100%; retain existing health 1280/375 cases. The existing 125% APP-00B/C exception remains EXCEPTION_ACCEPTED / NOT_TESTED in its original scope.

- Resolver/controller tests execute the exact public script, covering opposite OS preferences, invalid/blocked storage, write failure, missing media, stable snapshots and listener cleanup.
- Production browser tests assert theme before React mount under strict CSP; then operate the real selector, native controls and portal. Capture page identity, console/CSP failures, focus, long text, geometry and actual Light/Dark contrast pairs.
- Verify both custom font weights and Vietnamese/numeric glyph usage using Chromium font evidence; blocked-font fallback must remain usable without overflow.
- Retain health, dependency, PWA-build and real update tests. Run the theme unit suite and token/metadata/compiler gates as explicit steps in the two existing web CI jobs. The Issue 59 security contract freezes the package and Vite/Workbox input boundary.
- Attach screenshots, measurements and source/environment metadata to `web/test-results/` using the existing CI artifact uploads. Keep generated evidence separate from the committed implementation/specification. Record NOT_RUN for unobserved platform scopes; no accessibility certification, PREPROD, EV/P0 or business E2E promotion.

## 7. References

- [Owner decisions](../decisions/owner-uiux-2026-11-20.md), [adoption roadmap](adoption-roadmap.md), [theme contract](tailwind-v4-and-themes.md).
- [Component template](templates/component-spec.md), [screen template](templates/screen-spec.md), [compact sizing](components.md#compact-table-sizing).
- [Vite public assets](https://vite.dev/guide/assets.html#the-public-directory), [PWA static asset precaching](https://vite-pwa-org.netlify.app/guide/static-assets), [CSP](https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/CSP).
