# Issue #59 — Picomatch ReDoS security remediation gate

> **2026-11-19 advisory-withdrawal addendum.** GitHub withdrew GHSA-fc5q-39xc-vjpf on 2026-11-18 after an upstream dispute, without publishing a corrected `picomatch` version. On exact `main` HEAD `216ce71995e036b453775ccaa0097aaad98ea2c9`, [APP-00A #44323688504 attempt 2](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/44323688504) now reports **SUCCESS**: Node 26 `npm ci`, both required audits (0 findings), all frontend/PWA/Playwright and 296/296 .NET/SQL checks. This reflects the **changed advisory feed**, not a code patch or Security Owner waiver. Existing Issue #59 affected-version gate remains intentionally failing; see [updated input-boundary assessment](issue59-withdrawn-advisory-threat-assessment-2026-11-19.md). Historical failure observations below are **as of 2026-11-18**. PR #61 remains DRAFT / NO_MERGE pending independent final-HEAD review and explicit Owner disposition. No physical PREPROD, production or Pilot approval.

## 2026-11-19 — selected Owner B conditional risk path (NOT PATCHED)

Owner has **selected B** in [OWN-ISSUE59-B-20261119](../decisions/owner-issue59-conditional-risk-reclassification-2026-11-19.md), for the reviewed APP-00A synthetic/CI build scope **only**. This is not blanket acceptance of arbitrary glob patterns, a third-party fork, published frontend artifacts, PREPROD or production. The required independent Security review and exact-final-HEAD CI remain unfulfilled until separately evidenced.

The [machine policy](issue59-conditional-risk-policy.json) pins the **single official npm picomatch@4.0.7 development-only** resolution, exact registry tarball and integrity, no aliases, no Workbox runtime caching or untrusted/dynamic glob ingress, and **fail-closed expiration at 2026-12-19T00:00:00Z**. The old unconditional affected-version check is changed only under this explicit dated Owner decision to a scoped risk-policy test with hostile mutations. This does not claim the bytes were fixed. Node 26, both unmodified npm audits, source-bound Workbox/PWA/Playwright, SQL/.NET and all retained CI remain required. Any new external scanner signal, configuration change, expiry, new ingress or upstream patch requires renewed Security review; no automatic closeout.

Independent reviewer: **PENDING**. Issue #59 stays OPEN / PR #61 stays DRAFT until the separately reviewed final HEAD proves every gate. A green npm audit after advisory withdrawal is **ADVISORY_WITHDRAWN_AUDIT_CLEAN**, not **PATCH_VERIFIED**.

---

**Historical baseline (2026-11-18) — UNRESOLVED / BLOCKING / NO_MERGE.** This is an independent **security gate preparation PR**, NOT a claim that picomatch is fixed. Keep it separate from APP-02B.4 PR #58 and the canonical `business/` baseline.

## Verified baseline

- GitHub advisory: [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) / CVE-2026-112366, High, ReDoS with single-branch repeated extglobs such as `+(*)`.
- As of this investigation the latest official npm picomatch release was **4.0.7**, still listed as affected; advisory lists no verified fixed version. Do not substitute the unrelated older advisory GHSA-c2c7-rcm5-vvqj or call 4.0.7 patched.
- `web/package-lock.json` currently resolves `node_modules/picomatch@4.0.7` as a dev dependency; the full required `npm audit --audit-level=moderate` reported 11 HIGH. Production-only audit can pass while the full required gate fails.
- The original independent GitHub job on 2026-11-18 rejected any `picomatch <=4.0.7` by design and FAILED. Owner B permits only a newly reviewed, pinned, expiring, dev-only conditional risk-policy replacement; it does not establish a parser fix or waive npm audits.

## Historical strict-patch route A (not selected by Owner on 2026-11-19)

1. Obtain a maintainer-authenticated fixed npm release **or** an explicitly approved, independently reviewed patched distribution. Capture source diff, patch commit, publication/registry integrity, dependency graph, licensing, vendor trust and negative ReDoS test. Do not manufacture a version bump or alias to hide an advisory.
2. Update package manifest and lockfile using Node 26 / npm, with `npm ci` proving reproducibility. Preserve Vite, Tailwind v4, Workbox patched glob, PWA/Playwright and browser contracts; do not run `npm audit fix --force` or silently downgrade.
3. Require `node --test web/tests/picomatch-advisory.test.mjs`, `npm audit --omit=dev --audit-level=low`, `npm audit --audit-level=moderate`, Vite build, frontend unit tests, browser/PWA/Playwright and retained APP-00A Windows regression. **No ignored vulnerabilities or suppressed audit severity**.
4. Independently review source and artifacts on the exact final HEAD, then merge the security PR **only after its chosen Owner-approved security disposition and all required gates**. PR #58 was already separately merged source-only on 2026-11-18 with this debt explicitly OPEN; it cannot be rebased after merge and its exception is not inherited here. Retest the then-current main and relevant dependent increments instead. Keep #59 open until a genuine security closeout.

## Non-claims

No production dependency fix or safe package substitution, no PR #61 full security-gate PASS, no **new** APP-02B.4 waiver (the prior PR #58 source-only exception remains separate), no actual PREPROD evidence. This document is a fail-closed remediation contract and independent lockfile control, not security acceptance.
