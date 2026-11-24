# APP-00A Parallel CI Implementation Plan

> **For agentic workers:** Use the approved four-job design below. Keep implementation and independent review in this session; the Owner authorized execution on 24 November 2026.

**Goal:** Reduce APP-00A elapsed time by executing four complete validation groups concurrently while retaining the existing acceptance scope.

**Architecture:** Four independent `windows-2022` jobs (`core_sql`, `web_sales`, `app03`, `distribution`) retain whole test suites and their isolated scratch lifecycle. A small `validate` job depends on all four, always evaluates their results, and fails unless every required group succeeded. Repeated setup is intentional isolation; the first change leaves application and DEV runner scripts intact.

**Tech Stack:** GitHub Actions, PowerShell, .NET SDK 10.0.404 / Release / MTP, Node 26 with the existing 26.13.1 security floor, Python standard library for the result gate and its tests.

**Spec:** `docs/roadmap/app-05.md`, APP-05.0, plus the Owner-approved four-job proposal from 24 November 2026. Baseline: merge `7aada55d0889c4fd6d025816c76e7e4a83cd4102` (PR #144).

## Approved scope and global constraints

- Keep workflow name `APP-00A foundation`, the final check identifier/name `validate`, unfiltered `pull_request`, push to `main`, and `contents: read`.
- All four execution jobs start independently; none depends on another execution job. Preserve `windows-2022` and the current 45-minute safety budget for each.
- Preserve default checkout semantics. Keep reviewed PR source SHA distinct from the synthetic merge/workflow SHA; runner scripts retain their actual-checkout assembly source checks.
- Keep the full .NET test invocation, discovery floors, exactly-one-TRX rule, coverage, zero-failure/zero-skip checks, every independent SQL verifier, both npm audits, frontend/PWA checks, and all existing C/D/F/APP-03 runners.
- Keep nonce/ownership/instance/name guards, suite-local cleanup, conditional startup diagnostics, and evidence uploads on failure. Do not share scratch databases or run separate suites concurrently on one machine (API port 5116 is fixed).
- Application source, business snapshots, migration logic, runner scripts, test semantics, automatic retries, caching, concurrency cancellation, and path filtering are outside this first change.
- `VerifyAcceptance` and F09 All are not existing APP-00A gates; do not add or advertise them as retained gates.
- Preserve all baseline artifact path coverage. Use unique artifact names for new producing jobs, retaining `app00a-test-evidence` for core and `app00b-playwright-evidence` for the web/browser group. Document that the complete evidence set spans the four producers.
- Keep other workflows unchanged. Final-source CI and independent review are required; this implementation request does not independently authorize merging this new PR.

## Review focus

1. A failed, cancelled, skipped, missing or malformed required job must make `validate` fail, including failures before tests start.
2. Splitting must not lose a baseline step, condition, SQL verifier, report directory or test-discovery safeguard.
3. A cold worker must create its own correct Release outputs, dependencies and LocalDB environment without relying on another job's filesystem.
4. Source IDs, artifact names and scratch state must remain isolated and attributable to the exact tested checkout.
5. Local YAML/static checks do not establish Windows execution, protected branch settings, main verification or physical acceptance.

## Task 1: Partition workflow and implement fail-closed aggregation

**Files:**
- Modify: `.github/workflows/app00a.yml`.
- Create: `tools/verify_app00a_jobs.py`.
- Create: `tests/ci/test_app00a_jobs.py`.

**Interfaces:**
- Input to the gate: environment `APP00A_NEEDS_JSON`, populated directly from `${{ toJSON(needs) }}`.
- Required keys: exactly `core_sql`, `web_sales`, `app03`, `distribution`; each has `result: success` for a passing gate.
- Gate exits zero only for all four successful groups; nonzero for missing/extra groups, malformed payloads, and every non-success required result. Report useful per-job results without claiming test execution from statuses alone.
- `validate` uses `needs` for all four and `if: ${{ always() }}`, checks out the same source, runs the gate tests, then invokes the gate. Use a lightweight Ubuntu runner with Python already available and a five-minute timeout.

- [x] Write behavior tests for all-success, each job's failure/cancellation/skip, missing/extra jobs, missing result, malformed JSON and invalid payload types; exercise the actual CLI exit status as well as its validation function where useful.
- [x] Run `python3 -B -m unittest discover -s tests/ci -p 'test_*.py' -v` before implementation and record the expected failure.
- [x] Implement the small standard-library gate; rerun the focused tests and confirm success.
- [x] Move the original .NET/SQL block intact into `core_sql`, retaining business checks, LocalDB diagnostic condition, restore/build, provenance, TRX/coverage and SQL verifiers, then its `always()` evidence upload.
- [x] Move all frontend/audit/PWA steps and C.1–C.6 plus F01–F08 into `web_sales`, retaining needed .NET/Node/LocalDB setup, npm installation, Chromium, all checks and unique uploads.
- [x] Move App03A1 and the six APP-03 verification actions into `app03`, with independent required setup/build/dependencies/Chromium and its own evidence uploads.
- [x] Move D.1–D.3 into `distribution`, with independent required setup/build/dependencies/Chromium and its own evidence uploads.
- [x] Add `validate`; preserve all original commands and condition semantics. Where shared setup is copied, retain exact versions and startup diagnostics.
- [x] Parse YAML and compare all baseline non-upload step definitions against the partition. Every original validation command must still exist, with unchanged inputs/conditions; setup duplication and explicit upload partitioning are expected.
- [x] Run the gate tests and YAML/action lint where available. Confirm no source, business, runner or other-workflow diff. Commit the implementation after inspection.

## Task 2: Document mapping, validate source, and publish the PR

**Files:**
- Create: `docs/roadmap/app-00a-parallel-ci.md`.
- Modify: `docs/roadmap/app-05.md` only to link the approved follow-up, preserving APP-05.0's independent timeout/closeout history.

**Interfaces:** Consumes the actual job/artifact mapping from Task 1. Publishes the baseline, mapping, measured-versus-target distinction, verification commands and evidence boundary.

- [x] Document the four groups, independent startup, gate failure semantics, source identity and complete artifact set.
- [x] Record historical baseline run 45397979755 (28m59s, observed 355/355 .NET tests), target 10–14 minutes as an unverified estimate, and the queue/aggregate-runner-time tradeoff.
- [x] Run relevant existing business validators and local gate/YAML checks; record Linux limitations explicitly.
- [ ] Obtain an independent review against baseline `7aada55`, address substantive findings, and commit the final candidate.
- [ ] Push the feature branch and open a PR. Record final source SHA, workflow/run IDs, per-job durations, actual test/evidence counts and any failed attempts in the PR body, so recording execution receipts does not itself invalidate the tested HEAD.
- [ ] Confirm retained workflows and the four groups plus `validate` on final HEAD. Report actual results, limitations and the PR; do not claim this PR's post-merge main verification before it occurs.
