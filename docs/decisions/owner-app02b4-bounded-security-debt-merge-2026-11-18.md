# OWN-APP02B4-DEBT-01 — Bounded source-only merge with known security debt

**Owner direction dated 2026-11-18 (solo developer):** "Triển khai các bước tiếp theo như quyết định bổ sung thêm, những gì có thể coi là nợ kỹ thuật chưa có hướng giải quyết thì để lại, tôi muốn merge PR #58".

**Disposition:** `OWNER_REQUESTED_SOURCE_ONLY_MERGE / CONDITIONAL_SECURITY_DEBT_EXCEPTION`. This is a **new Owner policy amendment**, not a retrospective claim that old gates passed. Read with [OWN-APP02B4-SEC-01 and MERGE-01](owner-app02b4-code-only-2026-11-18.md) and [SOLO-01](owner-app02b4-solo-governance-2026-11-18.md). Only [PR #58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58) is within this exception, not PR #61 or any subsequent release/deployment.

## 1. Explicit scope and accepted risk

**The prior condition C-B4-05 (fix #59 and obtain full npm audit PASS before merging) and the *full APP-00A PASS* part of C-B4-04 are revised solely for CODE_ONLY source integration of PR #58**. Do not record these gates as PASS. Keep the existing GitHub Action **unchanged, required, and observably FAILING** as long as the advisory remains; do not suppress severity, override the lockfile merely to hide the finding, disable tests, force a major downgrade, remove an alert, or silently edit branch-protection.

[#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) is **OPEN HIGH-SECURITY DEBT**, not ordinary harmless technical debt. `picomatch@4.0.7` in `web/package-lock.json` is a **development-tree** transitive Vite/Workbox/tinyglobby dependency; the current full `npm audit --audit-level=moderate` reports **11 HIGH** from `GHSA-fc5q-39xc-vjpf` / `CVE-2026-112366`. As of this decision, the upstream official latest is affected, no independently verified patched npm release/compatible fork is accepted. `npm audit --omit=dev --audit-level=low` passed previously; that does NOT cure the dev-tree vulnerability. Because PR #58 adds only operator-only nonproduction source and **does not publish/deploy a frontend build**, Owner accepts the **source integration risk only**. This does not authorize running build tools on untrusted glob patterns, shipping a vulnerable artifact, real PREPROD use, production, pilot, or tenant business admission.

Separate [draft PR #61](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/61) remains OPEN/UNMERGED with an honest fail-closed security gate; required authentic patch provenance, bounded CVE negative tests, Node 26/npm ci, full audit and E2E must still be proven. No self-declared "fixed".

## 2. Required source-only compensating gates (all mandatory)

- **G-SRC-01:** Freeze and independently re-review *entire latest PR #58 diff* against current `main`; confirm `business/` and `business.lock.json` untouched, tenant API/Worker routes OFF, no production identity, real external provider, or destructive database action. Resolve all source-level security **code** findings, including cross-tenant *cross-role* SQL owner/finalizer/retirer collisions. Use HEAD-anchored review; changed HEAD invalidates signoff.
- **G-SRC-02:** B.4 Ubuntu/unit plus Windows disposable SQL LocalDB two-tenant security, lease, rollback, retirement, old-owner retake denial, cross-role collisions, current-epoch/RowVer, synthetic witness and fault cases PASS with **zero required failed/skipped** on exact final HEAD, artifact SHA and TestRunId retained.
- **G-SRC-03:** B.2 runtime CI and B.3 Identity/Linux+SQL CI PASS on exact final HEAD. Complete all APP-00A **pre-audit** .NET/business/two-tenant SQL oracle and regression gates successfully. Record separately that APP-00A's full npm audit **FAILED** and its downstream steps were skipped.
- **G-SRC-04:** Run a **separate** exact-final-HEAD Windows Node 26 web regression workflow to cover `npm ci`, **production npm audit**, Workbox/Tailwind dependency tests, frontend health/unit, TypeScript/Vite build, PWA generation/update and Playwright. This compensation **does not** replace or relabel the failing mandatory APP-00A full-tree audit.
- **G-SRC-05:** For #60, use Owner's authorized solo DBA and Security capacities in clearly separated review passes of current source, SQL effective privilege assumptions, Vault/SQL separation, disabled/retired finalizer and no post-retirement `TAKE OWNERSHIP`/`IMPERSONATE`; preserve the `SOLO_OWNER_SELF_ATTESTED / HUMAN_INDEPENDENCE_DEVIATION_DISCLOSED` label. An AI/CI review is *not* an additional human review. Physical SQL/Vault rights remain `NOT_VERIFIED` and are tracked openly by #60.
- **G-SRC-06:** Recheck open issues and exact PR HEAD/mergeability immediately before merge, preserve source/run URL/artifact hashes and the Owner's exception in PR; **do not override GitHub branch protections**. If a required, non-excepted gate fails or GitHub rejects merge, keep DRAFT and record why.

## 3. Deferred obligations / hard blocks

| Obligation | Integration disposition | Before real PREPROD or production |
|---|---|---|
| #59 picomatch `GHSA-fc5q-39xc-vjpf` | `ACCEPTED_SECURITY_DEBT_FOR_SOURCE_ONLY_MERGE`; OPEN HIGH; full APP-00A audit **FAIL** must be disclosed | Must fix or obtain a new, **explicit PREPROD-specific security risk decision**; no silent inheritance |
| #60 actual DBA/Security credentials, Vault ACL issuance, permission inheritance and physical denied owner retake | `SOURCE_CI_TESTED / SOLO_GOVERNANCE / PHYSICAL_NOT_VERIFIED`; keep issue OPEN | Independent real-environment SQL/credential evidence and signed operator acceptance; this source review is not proof |
| B.1 30 physical acceptance cases, G-B01..05, REAL-B4-01..03 | `NOT_RUN / NOT_VERIFIED`, not `N/A` or PASS | Strict physical independent witness required, later increment/B.9 |
| B.5/B.7/B.8/B.9 authorization, restore, audit, final physical gates | No expansion of APP-02B.4 scope | Separate increments/PR and evidence, no tenant route until explicitly authorized |
| Production P0, EV-08/AC-11, APP-02C/D/E, pilot/cutover | `NOT_AUTHORIZED / NOT_VERIFIED` | Full existing Business Baseline gates unaffected |

## 4. Status labels and evidence honesty

If and only if G-SRC-01..06 are satisfied and GitHub allows merge, the **maximum** closeout label is:

`MERGED / B4_GITHUB_HOSTED_CI_TESTED / KNOWN_SECURITY_DEBT_59_OPEN / SOLO_OWNER_REVIEW_DEVIATION_DISCLOSED / PHYSICAL_PREPROD_NOT_VERIFIED`.

Do **not** call the entire APP-00A pipeline green, `TENANT_LIFECYCLE_VERIFIED` or `PREPROD_FOUNDATION_VERIFIED`. Track both #59 and #60 separately; #59 is an active known high security vulnerability. Source merge does not close them.

**The Owner's instruction authorizes this scoped policy change and request to merge, but is not a fabricated individual DBA/Security code inspection or physical review signature.** Record actual evidence and technical findings first, and report any remaining blocking source defect instead of reclassifying it as debt. This decision supersedes only C-B4-04/05 for this one bounded source integration; all other Owner technical security gates survive.
