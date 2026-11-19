# Issue #59 — Independent evaluation of patched picomatch candidates

> **Historical candidate evaluation, updated 2026-11-19:** GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18; [APP-00A #44323688504 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44323688504) now reports **0 full-tree audit vulnerabilities and SUCCESS** at unchanged `main` HEAD. This is advisory-feed reclassification, **NOT** an upstream/third-party parser patch. All candidate/version and failed-audit observations below refer to **2026-11-18**. Review [current glob ingress and Owner decision assessment](issue59-withdrawn-advisory-threat-assessment-2026-11-19.md) before acting; no replacement fork has been accepted and PR #61 remains DRAFT.

**2026-11-18 | EVALUATION_ONLY / NO_DEPENDENCY_CHANGE / SECURITY_GATE_FAILING.**
Source [issue #59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59), independent [draft PR #61](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/61), retained APP-00A required full npm audit.

## Verified current state

- npm official `picomatch` dist-tag `latest` is **4.0.7** (npm version listing, 2026-11-18).
- Reviewed advisory [GHSA-fc5q-39xc-vjpf / CVE-2026-112366](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) still covers versions through 4.0.7, with no published upstream fixed version. The failure mechanism is exponential regex backtracking with single-branch repeated extglob globs (`+(*)`, `+(*a)`) and attacker-controlled strings/patterns.
- Canonical `web/package-lock.json` has `node_modules/picomatch@4.0.7` in the **development** tree (Vite/Workbox/tinyglobby/fdir transitive). APP-00A `npm audit --audit-level=moderate` reports 11 HIGH. Production-only audit PASS would **not** close the full-tree audit gate.

## Candidate comparison — not an installation decision

| Candidate | Source-level claim | Verified independent supply-chain/compatibility status | Decision |
|---|---|---|---|
| Official upstream picomatch | `4.0.7` latest; GHSA still affected, no fixed release | Official source, but vulnerable to still-published high advisory | **REJECT as remediation** |
| [`@rootio/picomatch@4.0.7-root.io.1`](https://osv.dev/vulnerability/ROOT-APP-NPM-CVE-2026-112366) | Root's downstream OSV entry **claims** its patched distribution fixes CVE-2026-112366 | Root-issued report is not an independent source-diff/registry-integrity/Node26 npm audit/PWA proof. The package is a **different publisher/trust boundary**. Exact npm tarball and provenance **NOT_VERIFIED** here | **REVIEW_CANDIDATE_ONLY** |
| [`@ryotsu/picomatch@4.0.7-a`](https://socket.dev/npm/package/%40ryotsu/picomatch) | Fork claims a guard for single-branch repeated extglobs | Third-party package details acknowledge some capture/literalization behavior changes; one maintainer; not approved for production tooling. Exact provenance, CI and compatibility **NOT_VERIFIED** | **REVIEW_CANDIDATE_ONLY** |
| Remove picomatch by changing frontend toolchain | Would remove the vulnerable dependency if all transitive paths disappear | Vite/PWA/tinyglobby/fdir/Workbox graph and browser tests must all be proven unchanged. No supported complete replacement identified | **UNPROVEN** |

**Important:** A different npm package name may evade a standard audit advisory even if the fork is identical to the vulnerable code. Therefore `npm audit PASS` is **necessary but not sufficient**. Independent diff, regression proof and exact-pinned registry integrity remain required. Do not claim a missing `picomatch` name or a new semver alias proves security.

## Proposed evaluation procedure (no automatic substitution)

1. Record independent Security Owner approval of the new publisher and exact tarball. Confirm `npm view <candidate@exact> name version dist.integrity dist.tarball dist.signatures --json` and compare to the official upstream package artifact. A registry integrity hash checks transport reproducibility, not who authored the patch.
2. Obtain, inspect and archive **complete source diff** for `lib/parse.js` and related scan/compile/matcher paths, licensing and new code/data. Confirm the actual repeated-extglob ReDoS fix, not only an advisory alias or a version-field change. Compare against upstream 4.0.7 and any earlier CVE regressions.
3. Test the actual behavior under Node **26.13.1+**, with isolated worker/process CPU/time cap for `+(*)`, `+(*a)` and nested/escaped variants and bounded pattern/string lengths. Match normal Vite/PWA/Workbox include/exclude glob semantics and negative/basename captures; do not disable default safety options.
4. If accepted, update `web/package.json` and `web/package-lock.json` with an exact pinned compatible artifact (no broad wildcard or forced downgrade). Verify `npm ci`, `npm ls picomatch`, **full** `npm audit --audit-level=moderate` and `npm audit --omit=dev --audit-level=low` with no added exceptions. Retain package integrity/provenance and source-bound CI artifact.
5. Required regression CI: TypeScript+Vite build, Workbox patched glob, frontend health, PWA/offline/update hook, Playwright Chromium, and **main APP-00A** Windows LocalDB plus APP-02B.2/B.3/B.4 on the eventual PR #58 final HEAD after separate security PR is independently reviewed and merged.
6. A security reviewer independent from the package selection/implementation signs explicit `PATCH_ACCEPTED` or `REJECTED`. If provenance/diff/safe behavior are missing, stop with `NOT_VERIFIED` and leave both PR #61 and issue #59 DRAFT/OPEN.

## Candidate metadata probe (not a security gate or audit waiver)

The CI workflow may use npm's public registry metadata to establish whether the claimed exact candidate tarballs exist. It must not install or override a candidate automatically, must not modify the authoritative lockfile, and must not claim candidate metadata equals a security patch. Candidate availability and `npm audit` failure are independent observations.

**Current disposition:** No candidate has passed these checks. No dependency lockfile or required CI audit changes have been made. This is an evaluation protocol, not security acceptance.

## Measured public npm registry availability — 2026-11-18

Independent [GitHub-hosted Node 26 candidate probe #44225144759](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44225144759), artifact `issue59-candidate-registry-metadata-only` (ID `14391947805`), returned:

- `npm view @rootio/picomatch@4.0.7-root.io.1`: **HTTP 404** from the public npm registry; `@rootio` patched distribution listed by Root's own OSV database is **not installable as this exact public npm package** in the CI environment. Do not use the Root version in package.json/lockfile or claim it is an available public npm fix. A private registry would constitute a separately approved new supply-chain boundary.
- `npm view @ryotsu/picomatch@4.0.7-a`: **metadata retrieved**, including public registry integrity metadata, from a public npm registry. This proves package metadata availability **only**; it does not verify patch source, upstream provenance, semantic compatibility, runtime correctness, or safety. The candidate has not been installed or permitted as an npm override.

The **required full-tree audit and vulnerable-lockfile jobs still fail**, correctly. Any future candidate evaluation must demonstrate independent code diff and sandboxed CVE-2026-112366 ReDoS tests plus complete Vite/PWA/Playwright/Workbox/APP-00A regression. Package-name aliasing solely to evade advisory matching is forbidden. Maintain issue #59 and PR #61 as **OPEN / DRAFT / NOT_PATCHED**.

## Source-level candidate diff evidence — GitHub-hosted isolated comparison

The [Node 26 source-diff workflow #44225741371](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44225741371) fetched public upstream `picomatch@4.0.7` and `@ryotsu/picomatch@4.0.7-a` with `npm pack --ignore-scripts` (no lifecycle script execution, no project lockfile override). Artifact `issue59-candidate-patch-source-diff-only` ID `14392322734`, SHA256 `c2b40715b053b8f5e99a733d66e2c2e35ab7b2db417357caf3d556c789d06b3d`, contains the upstream-vs-fork `lib/parse.js` diff and tarball SHA256 record.

**Observed `lib/parse.js` diff: 149 lines**, not a one-line version bump. It introduces `analyzeStarBranch` and changes `repeatedExtglobRecursion` to attempt to collapse safe repeated-star branches or flag potentially risky single-branch patterns. **It also changes unrelated semantics** in globstar wildcard matching (`.` to `[\\s\\S]`), `[!...] ` negation handling and a `**.js` fast path. These changes have potential Vite/Workbox/PWA glob/capture implications and require independent differential tests; no complete source review of every package file or publisher provenance has occurred.

**Evaluation result:** Candidate **technically exists and has a nontrivial source change**, but `PATCH_VERIFIED`, `SECURITY_APPROVED`, `COMPATIBLE`, `FULL_NPM_AUDIT_PASS` and `APP00A_PASS` all remain **NOT_VERIFIED**. Source diff is evidence for independent review, not acceptance. No manifest or lockfile changed; no unreviewed package was installed into application build. Root's advertised fork still returned npm public-registry HTTP 404, so it is not an immediately installable public npm substitution.

Actual release approval requires a separately authorized Security reviewer to inspect the whole source delta, tarball integrity and vendor trust, run bounded ReDoS proof and Vite/PWA/Playwright, then verify the **unchanged** full npm audit requirement on a new exact-lockfile CI HEAD. PR #61 stays DRAFT and #59 OPEN until then.

## Owner B selected 2026-11-19 — risk classification, not a fork

The Owner selected the bounded **withdrawn-advisory reclassification** route under [OWN-ISSUE59-B-20261119](../decisions/owner-issue59-conditional-risk-reclassification-2026-11-19.md), not the upstream-fork replacement route. No reviewed patched picomatch distribution has been substituted. The unchanged official picomatch@4.0.7 registry tarball remains pinned, development-only and unpatched. The [expiring, negative-tested scope policy](issue59-conditional-risk-policy.json) covers only APP-00A synthetic build/CI. Earlier fork analyses above remain historical observations, not installation recommendations. Independent Security review, final-HEAD CI and physical PREPROD/production NO_GO boundaries are retained.
