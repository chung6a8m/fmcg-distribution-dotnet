# OWN-APP00B-13-02 — 125% display-scale acceptance exception

**Decision:** ACCEPTED by repository Owner, **2026-11-16**. Applies only to APP-00B/C synthetic/nonproduction frontend UI acceptance, amending the 125% subsection of [OWN-APP00B-13-01](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13#issuecomment-6902392455).

## Scope and disposition

- **Remove** browser zoom / Windows display scaling **125%** from the roadmap acceptance matrix, Issue #13's required screenshots, and future APP-00B/C mandatory work. Do not silently reclassify it as a deferred required feature.
- **Retain** browser tests at **100%** for desktop and narrow **CSS viewport >=320px**: readable cards, absence of unintended horizontal scrolling, health contract and keyboard-visible PWA update action.
- **Retain** the user-confirmed service-worker **V1 → V2** update lifecycle at the same origin (new worker waits, visible prompt, no silent reload, click activates and loads V2).
- **Add Playwright** as the default frontend browser regression harness. Synthetic controlled tests are **additional evidence**; they do not alone constitute Owner acceptance of the manual gate.

## Risk explicitly accepted

At 125% scaling, the CSS viewport can fall below the `body min-width: 320px` floor, with possible clipped content / horizontal scrolling or keyboard/UI deviations. The Owner accepts that risk **for the current nonproduction APP-00B/C scope only**. Status is **EXCEPTION_ACCEPTED, NOT_TESTED**, never PASS. No automatic remediation deadline; scope must be reauthorized by Owner if user requirements later change.

**Still blocked:** Issue #13 remains open and APP-01A starts only after the remaining 100%/PWA evidence is reviewed and Owner acceptance is recorded. This exception is **not** accessibility certification and does not authorize EV/P0, offline writes, production, pilot or tenant go-live.

**Record:** [Issue #13 Owner comment](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13#issuecomment-6903234964).
