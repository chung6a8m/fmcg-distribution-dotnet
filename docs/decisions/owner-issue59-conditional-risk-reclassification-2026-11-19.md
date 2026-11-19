# OWN-ISSUE59-B-20261119 — Conditional withdrawn-advisory risk reclassification

**Decision date:** 2026-11-19. **Decision maker:** repository Owner. **Choice:** B, conditional and scoped; explicitly confirmed in conversation. **Security signoff:** PENDING (not implied by Owner's design choice).

## Approved direction and exact limits

Owner authorizes a **bounded reclassification assessment** of [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) in issue #59, rather than selecting an unreviewed fork or claiming an upstream patch. GitHub withdrew the advisory on **2026-11-18** after a dispute over attacker-controlled glob-pattern responsibility; the official locked npm distribution **picomatch@4.0.7 remains byte-identical and unpatched**. Technical ReDoS behavior under specially constructed patterns is not denied. The reclassification concerns whether that pattern-controlled attack can cross the narrowly inspected first-party build/CI boundary.

Permission is **only APP-00A SYNTHETIC/NONPRODUCTION frontend build and CI tooling** with trusted, checked-in literal glob patterns, pinned exact npm package integrity, Node 26 LTS and preserved evidence. It authorizes neither accepting arbitrary user/tenant/PR-supplied glob expressions nor publishing/deploying generated frontend artifacts. Production, physical PREPROD, Pilot, business admission and G-B01..05 remain **NO_GO / NOT_VERIFIED**. No authorization from prior APP-02B source-only exception carries into this decision.

**Independent Security reviewer and final-HEAD gate are still required before PR #61 can merge.** Selecting B does not itself mark those requirements met or authorize closing issue #59.

## Controlled policy and bounded lifetime

The machine-readable [Issue #59 conditional risk policy](../security/issue59-conditional-risk-policy.json) pins decision ID, GHSA/CVE, withdrawn classification, official npm registry tarball/integrity, exact **4.0.7 development-only** resolution, and non-deployment scope. The risk review **expires at 2026-12-19 00:00 UTC** (a conservative 30-day maximum implemented as a fail-closed engineering limit, not a permanent Owner waiver). Expiry causes the independent security gate to fail, even if the npm audit feed remains clean.

The existing full npm audit command and production audit retain exactly their original thresholds. No global npm override, false package version, alias, ignored advisory or relaxed CI check is permitted. A clean audit due to a withdrawn advisory is only **ADVISORY_WITHDRAWN_AUDIT_CLEAN**, never **PATCH_VERIFIED**.

## Mandatory acceptance checks

1. **C59-B01 — Exact dependency:** exactly one official locked picomatch@4.0.7, exact package path, npm resolved tarball/integrity, dev-only; no alias/fork, no transitive second resolution. Any manifest, lockfile, package version or integrity drift must fail and trigger re-review.
2. **C59-B02 — Bounded glob ingress:** checked-in Workbox precache glob must remain the inspected literal, runtime caching remains empty, no dynamic/untrusted pattern accepted. AST/static source checks and negative mutation tests must fail closed on injected patterns, spreads or extra plugin instances.
3. **C59-B03 — Safety-window expiry:** the source CI policy must reject dates at or after 2026-12-19T00:00:00Z, malformed dates, missing policy or changing policy ID/scope. No auto-renewal.
4. **C59-B04 — Retained npm security:** Node 26 npm ci, non-dev low-or-above audit and full moderate-or-above audit remain mandatory, unmodified, with recorded exact HEAD/results. If the same/follow-on advisory returns to npm audit, FAIL; no severity suppression.
5. **C59-B05 — Frontend and SQL regressions:** Vite/Workbox/Tailwind, UI unit, generated PWA/explicit update, Chromium Playwright, retained APP-00A Windows LocalDB/.NET and dependent APP-02B CI on exact final PR source; never infer skipped steps PASS.
6. **C59-B06 — Independent review:** reviewer independently inspects the complete PR diff, glob-flow/CI trust boundary, npm integrity, advisory dispute and downstream scanner divergences. The implementation author cannot self-attest an independent Security approval.
7. **C59-B07 — Change/review trigger:** immediate re-evaluation when GitHub/npm/OSV/NVD/Snyk/VulnCheck changes applicability, upstream publishes a fix, dependency/publisher/version/lock integrity changes, new runtime glob matching or untrusted pattern input is introduced, or CI/deployment topology changes. A discrepant active HIGH signal is triaged as OPEN security risk, not silently ignored.
8. **C59-B08 — Truthful disposition:** no false claim of patched bytes, no PREPROD/production admission, no automatic issue close while Security signoff or final-HEAD gates remain missing. A subsequent closeout, if approved, must say **WITHDRAWN_ADVISORY_SCOPED_RISK_ACCEPTED (NOT_PATCHED)** and retain a review-by date.

## Current cross-source security divergence (2026-11-19)

The inspected [GitHub GHSA](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) is WITHDRAWN, whereas [NVD CVE-2026-112366](https://nvd.nist.gov/vuln/detail/CVE-2026-112366) still shows a PUBLISHED, HIGH CNA finding and [OSV](https://osv.dev/vulnerability/GHSA-fc5q-39xc-vjpf) still shows the original HIGH advisory. The independent Security reviewer must explicitly assess **applicability to the trusted build-only glob boundary**, and document the residual scanner discrepancy; npm audit zero findings is not enough for security signoff. This paragraph is a review finding, **not** a new Owner grant to override unrelated scanners.

## Historical evidence and open review

- Main source commit `216ce71995e036b453775ccaa0097aaad98ea2c9`: [APP-00A run #44323688504 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44323688504) SUCCESS, full/production npm audit 0 and 296/296 SQL/.NET, 1440/1440 A11 SQL cells.
- PR #61 prior source `77ba095e8c49caa98302c9000bab4b0ad073f7b0`: [security CI #44434449231](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44434449231) intentionally FAIL for older unconditional version ban; [APP-00A #44434449146](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44434449146) SUCCESS. Replacing the unconditional ban with **more narrowly scoped, expiring and negative-tested** policy is subject to new CI and independent review; the historical failure was real, not waived.
- See [2026-11-19 threat assessment](../security/issue59-withdrawn-advisory-threat-assessment-2026-11-19.md). The inspected first-party build source currently does not accept external/user/tenant glob patterns; other dependency internals and hypothetical malicious contributors are not universally proven safe.
- Independent Security reviewer / signoff: **NOT_YET_RECORDED**. Final PR #61 exact-HEAD CI after implementation: **PENDING**. Physical PREPROD G-B01..05: **NOT_RUN**.

**Approval scope ends here.** The Owner's choice authorizes execution of this conditional review path and its strict guards. It does **not** pre-approve a CI failure, automatic merge, issue closure, release or any environment promotion.
