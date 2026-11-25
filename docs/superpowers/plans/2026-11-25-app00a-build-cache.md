# APP-00A Measured Build and Package Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce APP-00A preparation overhead only where observed timings and complete CI justify it.

**Architecture:** Measure NuGet caching and retain only demonstrated useful work. Archive-cache benefit was not material; the selected implementation keeps source-bound same-job Release Demo/API reuse and a fresh per-job NuGet package directory under RUNNER_TEMP after default→temp/miss→default observations. All existing restores, builds, gates and witnesses remain intact.

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
- Failed or carried-forward jobs must not create a misleading warm result; final archive-cache removal must retain every original restore/test/audit.
- Native LocalDB early failure and queue variance must not be counted as performance savings.

### Task 1: Same-job build reuse guard

**Files:** Create `scripts/Use-CiPrebuiltDemo.ps1`, `tests/ci/Test-CiPrebuiltDemo.ps1`; modify `scripts/Run-App02C1.ps1`, `scripts/Run-App03Dev.ps1`.

**Interfaces:** Consume `FMCG_CI_PREBUILT_SHA` (exact checkout SHA, enabled by Task 2 only after build success). Produce `Test-CiPrebuiltDemo -RepositoryRoot <path> -SourceCommit <git HEAD plus existing dirty marker>` returning false without opt-in, true after validation, or throwing for invalid opt-in. Emit a bounded reuse/guard elapsed log on success.

- [ ] Write executable negative and default-behavior regressions, run PowerShell test and observe RED for the missing guard.
- [ ] Implement the guard and wire it below APP-03's Stop return. Validate CI Windows, clean/source marker equality, Release/net10.0 Demo and API DLL ProductVersion SHA suffixes, plus the API project-reference copy in Demo output; require both executables' deps/runtimeconfig metadata. Missing or stale app-local API must be rejected before SQL/runtime launch. Preserve existing Remove exception when opt-in is absent.
- [ ] Run regression suite with real temporary files/assembly metadata and mocked process boundary; expect all pass. Parse modified PowerShell files; expect no syntax errors.

### Task 2: Workflow opt-in and measured cache selection

**Files:** Modify `.github/workflows/app00a.yml`; update `docs/roadmap/app-00a-parallel-ci.md` with a substantive follow-up link.

**Interfaces:** Produce Task 1's `FMCG_CI_PREBUILT_SHA` via `GITHUB_ENV` after successful initial Release build in web_sales/app03/distribution. Remove archive restore/save steps. A step after checkout in each worker writes `NUGET_PACKAGES` under `RUNNER_TEMP` to `GITHUB_ENV`; no cross-run persistence. This isolates the separately observed package-path improvement from archive caching.

- [x] Trial exact NuGet package caching without skipping restore/build/test; capture miss and exact-hit phases with archive overhead on source `fc2d79f`.
- [x] Remove all experimental NuGet configure/restore/save steps after the 12s warm path versus 13s cold restore did not establish material benefit; retain unconditional original commands and permissions.
- [ ] Based on the subsequent default-path 35–39s restore cohort, restore only four per-job temporary-package directory steps; keep archive restore/save absent. Validate this bounded path-only candidate once with full CI and record the observational limits.
- [ ] Add the guard regression to core_sql and opt-in setup after the existing builds in the three browser workers.
- [ ] Run actionlint, 15 aggregate-gate tests and business validators; compare original step commands/env/conditions/evidence to base. Expect no gate removal and no application/business changes.

### Task 3: Review and measured acceptance

**Files:** Implementation above; final receipts in PR body, intermediate logs under external scratch `cache-analysis`.

**Interfaces:** Consume final source/tree, GitHub run/job/cache outputs and baseline run 45509913554. Produce reviewed PR with cache miss/hit timings and complete CI status.

- [ ] Fresh independent review the entire diff and concrete failure classes; fix important correctness findings with regression evidence.
- [ ] Commit and publish exact verified tree on `ci/measured-build-cache`; create PR against current main.
- [x] Complete experimental cold CI (24/24 jobs) and capture exact-hit phases from a fresh-worker same-source/key rerun. Warm APP-03.5 hit the existing 3s session-proxy timeout; .6/.7 skipped, so its 244s truncated job is not a performance result or acceptance.
- [ ] Review the substantive archive-removal and path-only selection, then complete full CI on that final source, including APP-03.5/.6/.7 and all25 reuse calls. Do not add retries/timeouts or change runtime witnesses to make the benchmark pass.
- [ ] Keep build reuse and the temporary package directory only if final-source receipts support the selected behavior and restore benefit; update PR body with source/runs, experimental negative evidence, timing limits and all gates. Leave open for Owner review/merge.
