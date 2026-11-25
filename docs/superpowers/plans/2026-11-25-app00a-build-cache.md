# APP-00A Measured Build and Package Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce APP-00A preparation overhead only where observed timings and complete CI justify it.

**Architecture:** Cache NuGet package data in the four independent Windows workers while always restoring/building/testing. Reuse the same-job Release Demo/API build through an explicit source-bound CI opt-in in the two launchers. Retain all existing workflow gates and witnesses.

**Tech Stack:** GitHub Actions, PowerShell, .NET SDK 10.0.404, Node 26, Python verification.

**Spec:** `docs/roadmap/app-00a-build-cache.md`

## Global Constraints

- SYNTHETIC_NONPRODUCTION / CI; base `a7c8dc970d6aa15c9a3264b83c4c2e0f49a1434c` after PR #147.
- Keep SDK `10.0.404`, Node `>=26.13.1 <27`, Release, Windows 2022, discovery floor 317 and every existing witness.
- Do not edit business snapshots, application behavior, C# lifecycle witnesses, Vite hosting, LocalDB helper, concurrency or aggregate `validate` contract.
- No compiled outputs, database, evidence, browser or npm cache; no shared prebuild job.
- No merge; source-bound final receipts belong in the new PR body.

## Review Focus

- Dirty source or mismatched Demo/API at the same checkout must fail before runtime launch when reuse is explicitly requested.
- Default local execution must continue building, including original failure propagation and Remove behavior.
- APP-03 copied standalone Stop must remain independent of repository/helper; nested NextDay must inherit reuse.
- Cache key/input changes or a failed job must not create a misleading warm result; no cache-hit path may skip restore/test/audit.
- Native LocalDB early failure and queue variance must not be counted as performance savings.

### Task 1: Same-job build reuse guard

**Files:** Create `scripts/Use-CiPrebuiltDemo.ps1`, `tests/ci/Test-CiPrebuiltDemo.ps1`; modify `scripts/Run-App02C1.ps1`, `scripts/Run-App03Dev.ps1`.

**Interfaces:** Consume `FMCG_CI_PREBUILT_SHA` (exact checkout SHA, enabled by Task 2 only after build success). Produce `Test-CiPrebuiltDemo -RepositoryRoot <path> -SourceCommit <git HEAD plus existing dirty marker>` returning false without opt-in, true after validation, or throwing for invalid opt-in. Emit a bounded reuse/guard elapsed log on success.

- [ ] Write executable negative and default-behavior regressions, run PowerShell test and observe RED for the missing guard.
- [ ] Implement the guard and wire it below APP-03's Stop return. Validate CI Windows, clean/source marker equality, Release/net10.0 Demo and API DLL ProductVersion SHA suffixes, plus the API project-reference copy in Demo output; require both executables' deps/runtimeconfig metadata. Missing or stale app-local API must be rejected before SQL/runtime launch. Preserve existing Remove exception when opt-in is absent.
- [ ] Run regression suite with real temporary files/assembly metadata and mocked process boundary; expect all pass. Parse modified PowerShell files; expect no syntax errors.

### Task 2: Workflow package cache and opt-in

**Files:** Modify `.github/workflows/app00a.yml`; update `docs/roadmap/app-00a-parallel-ci.md` with a substantive follow-up link.

**Interfaces:** Produce Task 1's `FMCG_CI_PREBUILT_SHA` via `GITHUB_ENV` after successful initial Release build in web_sales/app03/distribution. A configure step after checkout in each worker writes `NUGET_PACKAGES` under `RUNNER_TEMP` to `GITHUB_ENV` (runner context is unavailable in job-level env). Use the exact dependency key; save uses restore's primary key.

- [ ] Add explicit NuGet restore/save steps; retain unconditional existing restore/build/test commands and read-only permissions. Save after verification only on success and non-exact-hit.
- [ ] Add the guard regression to core_sql and opt-in setup after the existing builds in the three browser workers.
- [ ] Run actionlint, 15 aggregate-gate tests and business validators; compare original step commands/env/conditions/evidence to base. Expect no gate removal and no application/business changes.

### Task 3: Review and measured acceptance

**Files:** Implementation above; final receipts in PR body, intermediate logs under external scratch `cache-analysis`.

**Interfaces:** Consume final source/tree, GitHub run/job/cache outputs and baseline run 45509913554. Produce reviewed PR with cache miss/hit timings and complete CI status.

- [ ] Fresh independent review the entire diff and concrete failure classes; fix important correctness findings with regression evidence.
- [ ] Commit and publish exact verified tree on `ci/measured-build-cache`; create PR against current main.
- [ ] Complete full CI, record cold miss+save and exact warm hit from a fresh-runner rerun at the same source/key. Retain all suites. Compare total restore/cache overhead and per-job/workflow elapsed; do not attribute unrelated runner variation to cache.
- [ ] Keep only measured useful optimization; record negative LocalDB evidence separately if encountered. Update PR body with source, runs, attempts, timing limits and all gates. Leave open for Owner review/merge.
