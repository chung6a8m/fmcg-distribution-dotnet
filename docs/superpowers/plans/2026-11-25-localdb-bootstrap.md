# LocalDB bootstrap implementation plan

> **For agentic workers:** execute the authorized CI improvement task by task; use deterministic regression tests and independent whole-branch review. This plan records implementation choices, not a new Owner approval requirement.

**Goal:** make GitHub-hosted Windows LocalDB initialization explicit, bounded and diagnosable so first-use connection timing does not require rerunning a complete CI job.

**Architecture:** one PowerShell helper inspects the existing automatic instance, explicitly creates it only when positively uncreated, starts it when stopped, and verifies LocalDB readiness. Only a first-connection login timeout after verified Running state permits up to three read-only probes within a 60-second overall deadline. Unclassified creation/start failures remain failures.

**Tech stack:** PowerShell on existing `windows-2022`, SqlLocalDB/SQLCMD, current SQL Server 2019 LocalDB runtime; existing .NET/Node pins and all validation scripts remain unchanged.

**Spec:** Owner request of 25 November 2026 (CI improvement 4), and [LAB-E03](../../roadmap/app-02b-local-preprod/04-evidence-and-closeout.md#lab-e03) constraints. Baseline `ac2ca0d5f0f28b14cb7bc186ae3b8628b7a9bc59`, after PR #146.

## Evidence and implementation boundary

- New-main B4 run `45499265441`, job `138817869787`, and prior-main B5 run `45445278577` attempt 1 fail during implicit automatic-instance creation, before SQL tests. B5 attempt 2 succeeds on unchanged source and another worker.
- APP00A runs `45150486380` and `45137989550` have inspected diagnostic artifacts `14805903355` and `14800342549`: initially uncreated, failed automatic creation, then instance exists but is Stopped, version `15.0.4382.1`, no SQL process, repeated Event 528 and recent error logs. Native text records a failure despite reported exit zero.
- Those artifacts do not retain the native engine cause. Event 528 and a successful rerun do not establish a transient race or resolved corruption. Underlying engine root-cause closure remains `INVESTIGATION_BLOCKED`; issue #75 and the separate HTTP 500 investigation stay open.
- Microsoft documents explicit `create MSSQLLocalDB` before `start` and first-use login timing. Separate create/start and bounded readiness are justified orchestration changes; no blanket retry of `AutomaticInstanceCreationFailed` is authorized by this evidence.

## Global constraints

- Only the exact automatic instance `MSSQLLocalDB` on GitHub-hosted Windows; never accept a remote server or another instance. Existing database ownership/nonce guards are unchanged.
- No delete/reset/stop/repair of an instance, no engine install/upgrade, no migration, HTTP, ledger or test retry.
- Preserve current LocalDB 15.0 runtime family; positively verify installed/instance version and fail on mismatch rather than silently switching versions.
- Preserve PR concurrency, four independent APP00A workers, `validate`, workflow triggers/permissions, acceptance steps, evidence paths and minimum test floors.
- Native command failures, timeouts and recognized error text cannot be hidden by exit zero or a later successful probe. Failed-stage history survives success/failure in a redacted source-bound JSON receipt.
- A 60-second monotonic budget covers initialization/readiness; at most three readiness probes, each with finite login/query/process timeouts. Launcher cancellation must not kill a SQL engine process tree.
- All test doubles replace only the Windows native boundary. Tests execute the real PowerShell orchestration and inspect results/history/side effects.

## Review focus

1. Combined login-timeout plus permission/creation/runtime error must fail without retry.
2. Cold instance is distinguishable from inaccessible, malformed or foreign instance metadata.
3. A successful start followed by Stopped, wrong version or wrong LocalDB identity cannot earn readiness.
4. Deadline exhaustion does not issue another command or destroy a child SQL engine.
5. Evidence is retained before tests begin, contains source/run/job identity, and exposes no raw paths or secret payloads.

## Task 1 — deterministic bootstrap and regressions

**Create:** `scripts/Initialize-LocalDb.ps1`, `tests/ci/Test-LocalDbBootstrap.ps1`.

**Modify:** `scripts/Collect-LocalDbDiagnostics.ps1` only where required for a safe shared native-probe boundary and bounded numeric SQL error diagnostics.

**Interfaces:** entrypoint `./scripts/Initialize-LocalDb.ps1` consumes `CI_LOCALDB_SOURCE_HEAD_SHA`; defaults to `MSSQLLocalDB`, deadline 60 seconds and installed/instance version family 15.0. Dot-sourcing exposes `Initialize-LocalDb` for controlled tests. It returns a typed result/history; the entrypoint writes a unique JSON receipt under `TestResults/LocalDbBootstrap/` and exits nonzero on failure. Existing LAB-E02 collector remains available on failure under `TestResults/LAB-E02/`.

- [x] Write/run a regression showing the old implicit start does not explicitly create a positively uncreated instance; cover failure-text/exit-zero and native exit capture before replacing it.
- [x] Implement inspect → create if uncreated → inspect/version check → start if stopped → inspect Running → read-only master/IsLocalDB readiness.
- [x] Add cold/warm, missing engine/tool, access denial, version mismatch, foreign instance, malformed output, start failure, stopped-after-start, readiness retry/exhaustion, deadline, redaction and process-cleanup regressions. Only login timeout with a still-running verified instance is retryable.
- [x] Retain typed stage/attempt history and write source-bound receipts on both success and failure; keep raw native output private to classification.
- [x] Improve future diagnosis with bounded numeric SQL error signatures only, without raw event/log payload or repair actions; test redaction and byte bounds.
- [x] Run PowerShell regression suite and existing APP00A gate tests; preserve red/green evidence.

## Task 2 — workflow integration and hosted verification

**Modify:** APP00A and B3/B4/B5/B6/B7/B8 workflow bootstrap steps, existing evidence upload path lists as necessary, and concise operational documentation.

- [x] Replace the ten duplicated bootstrap sequences with the helper while retaining SDK/source guards. Add failure-only LAB-E02 collection for B3–B8. Preserve all business/test/witness commands.
- [x] Run deterministic helper regressions before core_sql bootstrap; verify one real warm initialization after its cold initialization. Every helper invocation emits its own receipt.
- [x] Ensure existing always-upload artifacts include `TestResults/LocalDbBootstrap/` and `TestResults/LAB-E02/`, including startup failures before a test creates results.
- [x] Validate all workflow YAML with actionlint and compare original acceptance/gate steps to baseline; independent review before publishing.
- [ ] Open a substantive PR from the reviewed tree; require all triggered workflows/jobs on the exact final head. Inspect cold/warm receipts, effective attempt histories and original failure evidence. If native failures persist, use their diagnostics rather than masking them with a rerun.
- [ ] Store final CI receipts in the PR body. Do not create a commit merely to acknowledge those receipts; do not merge without a separate request.

## Primary references

- [Microsoft: explicit LocalDB create/start](https://learn.microsoft.com/en-us/sql/connect/php/php-driver-for-sql-server-support-for-localdb)
- [Microsoft: automatic-instance first connection](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb)
- [Microsoft: SqlLocalDB utility](https://learn.microsoft.com/en-us/sql/tools/sqllocaldb-utility)
- [Microsoft: sqlcmd timeout and exit options](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility)