# Issue #16 — Workbox/glob security disposition

**Date:** 2026-11-16. **Scope:** APP-00A React PWA build toolchain on Node 24; synthetic/nonproduction only. No changes to `business/`, `business.lock.json`, backend tenancy, scratch DB guards or offline business commands.

## Problem and selected remediation

At the baseline `main` SHA `1a747d8d9c9390de10b0ed3ccc28831b129c4eef`, the npm lock resolved:

`vite-plugin-pwa@2.0.0 → workbox-build@7.4.1 → glob@11.1.0`

`glob@11.1.0` is affected by **GHSA-3g3m-2fjr-6pw4 / CVE-2026-118773** (abortable directory walk can continue traversing after cancellation; availability impact). Patched releases begin with **14.0.2**.

As checked on 2026-11-16, `vite-plugin-pwa@2.0.0` still depends on `workbox-build@^7.4.1`, whose published manifest requires `glob@^11.0.1`; there is no compatible patched Workbox release to select through a routine lockfile refresh. Avoid an *unscoped* global override.

**Implemented:** narrow npm override for `workbox-build > glob` to exact `14.0.6`; regenerated and committed `web/package-lock.json` on Node 24. The dependency remains build-only (`dev: true`) and is not included as an application runtime package. This cross-major override is a temporary compatibility bridge, not a claim of upstream support; maintain PWA smoke tests and revisit when Workbox or its maintained replacement ships a compatible fix.

## Actual locked-tree audit evidence

Audit and install ran on **Node 24.22.2 / npm 11.19.1** in [GitHub Actions run #43770601901](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/43770601901), including the four machine-readable audit JSON files in the `issue16-audit-evidence` artifact.

| Scan | Main baseline | Patched lockfile |
| --- | --- | --- |
| `npm audit --omit=dev` | **0** findings | **0** findings |
| `npm audit` | **6**: 4 Moderate + 2 Low | **4 Low**, **0 Moderate/High/Critical** |

Before: the Moderate advisories were reported against `glob`, `minimatch` and their parent `workbox-build` / `vite-plugin-pwa` entries. After: remaining Low entries are `@rollup/plugin-node-resolve`, `deepmerge`, `workbox-build` and `vite-plugin-pwa` (npm's transitive propagation, not four independent shipped vulnerabilities).

**Residual Low disposition:** remains an upstream build-tool finding. It is *not* declared a clean full-tree audit, accepted production risk, or a reason to enable offline business writes. Reassess with a compatible upstream Workbox/PWA release or by **2026-12-16**; escalate any Moderate-or-higher regression immediately. CI now fails for findings in the npm **non-dev dependency tree** (Low or higher) or the **full npm dependency tree** (Moderate or higher). The `--omit=dev` scan is based on npm package classification, **not** a scan of every byte shipped in the PWA: Workbox packages are marked `dev: true`, but some of their runtime code is bundled into the generated service worker. The full-tree gate covers these packages at Moderate or higher; Low findings remain visible and explicitly dispositioned. The remaining Low build-only findings are visible and nonblocking for this nonproduction foundation.

## Reproducibility and behavior checks

- `npm install --package-lock-only --ignore-scripts --no-audit --no-fund` and then `npm ci --no-audit --no-fund` **PASS** on Node 24.
- `npm ls glob workbox-build vite-plugin-pwa --all`: `workbox-build@7.4.1 → glob@14.0.6 overridden`; no other `glob` resolution in the committed npm lock.
- The original **4/4** frontend health API contract tests **PASS**; TypeScript/Vite PWA build **PASS**.
- The PWA artifact tests check the generated `dist/sw.js` for the `SKIP_WAITING` explicit-update handler, assert that the Workbox precache manifest includes `index.html` and every emitted JavaScript/CSS file in `dist/assets/`, and validate the generated `dist/manifest.webmanifest`. These checks run after the Vite build in CI.
- **Not performed:** real-browser update-prompt confirmation at 100%/125%. This remains a separate manual acceptance in [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13). Generated artifact smoke tests must not be presented as a substitute.

## Decision and follow-up

The specific `glob@11.1.0` finding has a **patched-version remediation**, backed by a locked tree and CI verification. Retain the scoped override only until a supported upstream migration can be tested; do not silently remove the security tests when changing PWA tooling. Issue #16 can be closed **after its PR is reviewed and merged**. This disposition does not authorize APP-01A, EV/P0 or production.

References: [glob advisory](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4), [Workbox upstream dependency issue #3599](https://github.com/GoogleChrome/workbox/issues/3599), [PWA tooling modernization direction](https://github.com/vite-pwa/vite-plugin-pwa/issues/933).
