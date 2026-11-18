# Issue #59 — Picomatch ReDoS security remediation gate

**2026-11-18 — UNRESOLVED / BLOCKING / NO_MERGE**. This is an independent **security gate preparation PR**, NOT a claim that picomatch is fixed. Keep it separate from APP-02B.4 PR #58 and the canonical `business/` baseline.

## Verified baseline

- GitHub advisory: [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) / CVE-2026-112366, High, ReDoS with single-branch repeated extglobs such as `+(*)`.
- As of this investigation the latest official npm picomatch release was **4.0.7**, still listed as affected; advisory lists no verified fixed version. Do not substitute the unrelated older advisory GHSA-c2c7-rcm5-vvqj or call 4.0.7 patched.
- `web/package-lock.json` currently resolves `node_modules/picomatch@4.0.7` as a dev dependency; the full required `npm audit --audit-level=moderate` reported 11 HIGH. Production-only audit can pass while the full required gate fails.
- The new independent GitHub job reads the exact lockfile and fails if any `picomatch <=4.0.7` remains. This is **necessary but not sufficient** for remediation. It intentionally FAILS on the vulnerable current baseline; do not merge this preparatory PR while required CI fails.

## Required real fix in this *separate* frontend PR

1. Obtain a maintainer-authenticated fixed npm release **or** an explicitly approved, independently reviewed patched distribution. Capture source diff, patch commit, publication/registry integrity, dependency graph, licensing, vendor trust and negative ReDoS test. Do not manufacture a version bump or alias to hide an advisory.
2. Update package manifest and lockfile using Node 26 / npm, with `npm ci` proving reproducibility. Preserve Vite, Tailwind v4, Workbox patched glob, PWA/Playwright and browser contracts; do not run `npm audit fix --force` or silently downgrade.
3. Require `node --test web/tests/picomatch-advisory.test.mjs`, `npm audit --omit=dev --audit-level=low`, `npm audit --audit-level=moderate`, Vite build, frontend unit tests, browser/PWA/Playwright and retained APP-00A Windows regression. **No ignored vulnerabilities or suppressed audit severity**.
4. Independently review source and artifacts on the exact final HEAD, merge the security PR first, then reconcile/retest B.4 PR #58 against the repaired main branch. Keep #59 open until that is complete.

## Non-claims

No production dependency fix, no safe package substitution, no frontend gate PASS, no APP-02B.4 merge waiver, no actual PREPROD evidence. This document is a fail-closed remediation contract and independent lockfile control, not security acceptance.
