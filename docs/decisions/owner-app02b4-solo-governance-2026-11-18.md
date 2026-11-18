# OWN-APP02B4-SOLO-01 — Solo-developer Owner / DBA / Security governance

**Owner decision date:** 2026-11-18 (Asia/Bangkok); **decision source:** the Owner's explicit instruction in the project discussion: *"Coi như tôi được ủy quyền luôn ở các vai này. Tài liệu để ghi nhận lịch sử, tiếp tục giữ hướng solo dev."*

**Status:** `OWNER_APPROVED_SOLO_ROLE_AUTHORIZATION / SOURCE_ACCEPTANCE_PENDING`.
Applies to [APP-02B.4 PR #58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58), [privilege-retirement finding #60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60), and their historical documentation. This addendum does **not** retroactively claim a signoff, executed PREPROD validation, or merge.

## 1. Explicit organizational-role delegation

The **same sole human developer / repository Owner** is authorized to serve as:
- **Owner:** approve the bounded design, accept or reject documented residual risk, decide source-only merge when all required gates pass.
- **DBA reviewer / operator:** review and approve the SQL ownership, credential issuance/retirement, role inheritance, grants, session termination, migration and restore procedures.
- **Security reviewer:** independently of the implementation *phase*, perform a separate adversarial review pass of threat model, Vault/network isolation, permissions, negative evidence and residual risk.

No additional GitHub identities, reviewer requests, external personnel, or fictitious independent signatures are required for this **solo source-only governance workflow**. The role labels identify *different responsibilities performed by one person*, not distinct reviewers. Never write `TWO_INDEPENDENT_HUMANS_REVIEWED` or forge a second review.

This decision **supersedes only** the *different-human DBA/Security reviewer assignment* expectation in the older [DBA60 acceptance handoff](../security/app-02b4-dba-security-independent-acceptance.md) and C-B4-02's human-role staffing for **source-only** code review. It does **not** remove separate adversarial inspection, zero blocking findings, explicit evidence review, or current-HEAD signoff.

## 2. Non-negotiable technical separation

The same person may operate the system sequentially, but the software must still enforce and test distinct **SQL Server principals, Vault tokens, ACLs and state transitions** for requester, approver, provisioner, finalizer, DBA-retirer, scoped verifier and security suspension. Do not replace SQL-authoritative Maker/Checker or privilege fencing with an Owner flag, reuse secrets across roles, or give API/Worker DBA credentials.

The solo human operator must explicitly identify the current **role and credential** for each run. Record actor ID, UTC timestamp, TenantId, approved binding/epoch/lease/operation, role-scoped token version identifier (never the secret), code HEAD, migration SHA-256, grant/owner before-and-after, negative tests and expected/actual/diff.

A single human's self-review is **not independently human-reviewed**. The audit ledger must carry `SOLO_OWNER_SELF_ATTESTED` and disclose that residual organizational separation-of-duties risk.

This decision does not reinterpret or waive any unrelated business Maker/Checker requirements in the frozen APP-02 business baseline or B.1/B.5/B.6 acceptance contract.

## 3. Code-only acceptance protocol for #60 and PR #58

Use sequential, clearly separated review passes on the exact final PR HEAD:

1. **Developer pass:** implement and resolve all source findings; freeze specific commit SHA; no code changes during acceptance. Compare all PR files to main, confirm `business/` and `business.lock.json` unchanged.
2. **DBA role pass (Owner acting as DBA):** inspect effective SQL permissions (not only explicit grants), database owner SID, original-login sessions, login-disable/revoke/retire credentials, loss-of-ACK and epoch races. Record DBA60-01..10 expected/actual/diff and reproducible SQL/CI evidence.
3. **Security role pass (same Owner acting as Security):** adversarially inspect Vault/SQL trust boundaries and TM-05..10, denied peer/owner retake, role/token reuse, source and artifact hashes, residual trusted-DBA privileges. A tool-assisted/AI second-pass review is useful, but is **not** an additional human reviewer.
4. **Owner disposition:** record a specific final-HEAD verdict `SOLO_ACCEPT_CODE_BOUNDARY`, `REQUEST_CHANGES` or `BLOCKED_MISSING_EVIDENCE`, explicitly stating the DBA and Security checks were performed by the **same person**. Do not automatically infer `SOLO_ACCEPT_CODE_BOUNDARY` from this role authorization, a CI PASS or a previous review.
5. **Merge gate:** retain [OWN-APP02B4-MERGE-01](owner-app02b4-code-only-2026-11-18.md) C-B4-01, -03, -04, -05, -06 in full; adapt **C-B4-02 staffing only** to documented Owner-self-attested DBA/Security technical review. Require zero blocking findings and full APP-00A `npm audit --audit-level=moderate` plus all required B.2/B.3/B.4/frontend/PWA/Playwright CI PASS on the final HEAD. Neither [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59) nor current unreviewed [#60](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/60) may be silently waived. A changed HEAD invalidates any earlier acceptance.

## 4. Separate physical PREPROD obligations

`SOLO_OWNER_SELF_ATTESTED` may authorize a bounded **code-only merge** after gates, but is not evidence of actual PREPROD operations, real two-tenant grants or B.1's independently witnessed 30 cases.

`REAL-B4-01..03`, physical `G-B01..05`, session/revocation/restore and actual `TENANT_LIFECYCLE_VERIFIED` all remain `NOT_RUN / NOT_VERIFIED` until independently reproducible, source-bound physical evidence is actually executed. Where a contract still calls for a *distinct independent person*, record an explicit **organizational independence deviation** and obtain a separate Owner acceptance disposition for that physical gate; never quietly relabel solo operations as independent witnesses.

No production/pilot/tenant-business routing/APP-02C/D/E authorization results from this role delegation. Production P0 and real AC-11/EV-08 remain `NOT_VERIFIED`.

## 5. Historical and acceptance status

| Item | Disposition as of this decision |
|---|---|
| Solo Owner, DBA and Security responsibilities | `AUTHORIZED_SAME_HUMAN` |
| Distinct humans for B.4 source-only DBA/Security roles | `NOT_REQUIRED_BY_OWNER / INDEPENDENCE_DEVIATION_DISCLOSED` |
| SQL/Vault principal and credential separation | `REQUIRED_UNCHANGED` |
| #60 signed code-boundary technical review on exact HEAD | `PENDING_OWNER_REVIEW` |
| #59 full-tree dependency security audit | `BLOCKED_UNPATCHED` |
| #58 merge authorization | `CONDITIONAL_ONLY / NOT_ELIGIBLE_YET` |
| Physical PREPROD and frozen G-B01..05/30 | `NOT_VERIFIED` |

**Historical preservation:** Earlier decisions, reviews, PR comments, and the originally drafted independent-human DBA/Security acceptance document are retained as historical evidence. This addendum is the **newer controlling Owner staffing disposition** for B.4 source-only work, not a rewrite of past events or tests.
