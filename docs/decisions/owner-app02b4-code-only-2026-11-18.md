# APP-02B.4 — Two additional Owner decisions (2026-11-18)

**Status: OWNER_APPROVED (design and conditional source-merge policy only); IMPLEMENTATION/ACCEPTANCE PENDING.**
Repository: `chung6a8m/fmcg-distribution-dotnet`; implementation PR [#58](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58).

This record supplements, without amending, [OWN-APP02B-01..10](owner-app02b-2026-11-17.md), the [APP-02B.1 architecture/security contract](../roadmap/app-02b1-architecture-security-contract.md), its [threat model](../security/app-02b1-threat-model.md) and [30-case independent acceptance matrix](../security/app-02b1-acceptance-evidence.md).

## OWN-APP02B4-SEC-01 — Independent DBA-controlled Finalizer Automation

**Owner selection: Option 2 (OWNER_APPROVED).**

1. Keep tenant request/approval, initial SQL `CREATE DATABASE` and tenant marker provisioning, ownership finalization, and independent verification as separate trust-boundary steps. A dedicated **DBA-controlled finalizer** transfers ownership only for an exact approved TenantId, deterministic DB name, current immutable SQL owner/provisioner identity, BindingId/BindingVersion, SecurityEpoch, and fenced operation identity.
2. The finalizer uses an independent Vault-scoped credential and the minimum SQL authority for the approved change; it never receives API/Worker credentials and cannot assume Control Plane approval power, read arbitrary tenant business data, transfer peer/Control Plane ownership, adopt unknown databases, or automatically DROP a database. No self-approval or shared requester/approver/provisioner/verifier credentials.
3. Crash/retry and concurrent suspension/revocation fail closed. An unfinished or stale finalization leaves tenant **non-routable**. Finalizer completion must be durable, auditable, idempotent under its approved operation key and independently read back. Verifier must still validate the real `sys.databases.owner_sid`, approved current SQL binding, original DDL principal denied direct tenant SELECT/UPDATE and scoped probe principal denied peer/control access and own marker UPDATE **before** SQL-authoritative activation.
4. Implement a dedicated finalizer path, negative SQL tests and runbook/security evidence. The manual DBA transfer represented in the current PR branch is an **interim design**, not finalizer-complete acceptance. Actual SQL privileges, Vault/role grants, DBA change ticket and independent human Security/DBA approval remain **NOT_VERIFIED** in physical PREPROD.

Owner decision evidence: [PR #58 decision comment](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/58#issuecomment-6965474334).

## OWN-APP02B4-MERGE-01 — Bounded CODE_ONLY_MERGE

**Owner selection: Option 2 (OWNER_APPROVED, conditional, not an immediate merge authorization).** Allow a source-only integration of PR #58 **only after all** of the following have passed on the *exact final PR HEAD*:

| Gate | Required witness | Current disposition when decision was recorded |
|---|---|---|
| C-B4-01 Finalizer completeness | Dedicated independently authenticated DBA-controlled finalizer, SQL-authoritative binding/lease/epoch/CAS, crash/retry/peer/privilege negative tests; no public API/Worker tenant route | NOT_VERIFIED; interim manual handoff source only |
| C-B4-02 Security and code review | Fresh independent review of full final diff, explicit threat model TM-05..10 closure within B.4 scope, zero unresolved blocking findings, separate Security/DBA disposition for privilege boundary | NOT_VERIFIED |
| C-B4-03 B.4 CI and artifacts | Ubuntu unit, Windows two-tenant LocalDB, actual SQL privilege/ownership and verifier negatives, redacted TestRunId, exact source/workflow/migration hashes and artifact review; zero required failed/skipped tests | Earlier B.4 CI PASS on `192d7a2`, invalidated by any later HEAD edit |
| C-B4-04 Retained regressions | APP-02B.2, B.3 and **APP-00A** exact-final-HEAD PASS; no disabled jobs, altered audit severity, omitted browser/PWA/Playwright, or inherited old-HEAD result | NOT_MET: APP-00A #44207942256 FAILED |
| C-B4-05 Dependency security | Independently remediate [#59](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/59); required full npm dependency-tree `npm audit --audit-level=moderate` must PASS, using a reviewed compatible fix; do **not** suppress advisory, run `--force` blindly, or waive high findings | NOT_MET; upstream picomatch GHSA-fc5q-39xc-vjpf |
| C-B4-06 Owner/source integration | This explicit Owner conditional disposition attached to PR; reviewers confirm all above gates, re-check latest HEAD/branch and mergeability immediately before merge; prevent stale-HEAD merging | Conditional only; not authorized to merge before gates pass |

**Maximum post-merge claim:** `MERGED / B4_GITHUB_HOSTED_CI_TESTED` (code integration only). **Not** `TENANT_LIFECYCLE_VERIFIED`, `PREPROD_FOUNDATION_VERIFIED`, or any physical PREPROD gate PASS.

## Non-waived boundaries and subsequent evidence

- **REAL-B4-01..03**, physical two-tenant SQL/Control Plane/Vault/DBA operator evidence, independent Security/DBA review, real RUNTIME-01/02, and frozen **B01-01..06, B02-01..06, B03-01..06, B04-01..06, B05-01..06** remain `NOT_VERIFIED` / `NOT_RUN` until actual independent PREPROD execution. Existing CI does not replace these.
- B.5 authorization/revocation, B.7 PITR/restore, B.8 immutable audit, B.9 physical independent acceptance keep their own PRs, responsibilities and gates.
- No tenant business writes/routing, production deployment, pilot, cutover, financial provider, APP-02C/D/E or A/B/Mixed mode admission. Original production P0 gates and real AC-11/EV-08 are `NOT_VERIFIED`.
- This B.4-specific code-only exception does **not** retroactively modify B.2/B.3 Owner decisions or waive any APP-00A security finding. Approval of this *policy* is not evidence that its preconditions have been met.

## Subsequent Owner staffing addendum — OWN-APP02B4-SOLO-01 (2026-11-18)

The Owner subsequently authorized **one solo developer to act in Owner, DBA reviewer and Security reviewer capacities**, without assigning other GitHub users. See [the versioned solo-governance decision](owner-app02b4-solo-governance-2026-11-18.md). This supersedes **only the different-human reviewer staffing expectation for C-B4-02 source-only review**. The DBA and Security checks must still be performed as separate documented evidence-based passes, and the signoff must explicitly be marked `SOLO_OWNER_SELF_ATTESTED`, **not** `TWO_INDEPENDENT_HUMANS_REVIEWED`. This decision is role authorization, **not** automatic source/security acceptance of PR #58. C-B4-01/C-B4-03..06, exact-final-HEAD review, all CI and full npm audit, #59 and technical #60 findings, separate SQL/Vault principal scopes and non-PREPROD claims remain unchanged.

Original Owner decisions and their historical independent-review language above are preserved verbatim; this later dated addendum controls only the source-level human staffing exception.
