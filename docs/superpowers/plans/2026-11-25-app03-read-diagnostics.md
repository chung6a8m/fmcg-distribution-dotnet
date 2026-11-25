# APP-03 read diagnostics implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the pending HTTP/SQL phase observable when APP-03.4/.5 browser assertions fail, then complete the authorized review, verification and merge of PR #148.

**Architecture:** An opt-in Playwright fixture records allowlisted browser request lifecycle fields, leaving the approved Vite configuration byte-identical. Awaited global setup starts a bounded PowerShell SQL observer for the already-created, nonce-owned tenant-a database and records readiness before browser tests; global teardown reaps it. The reporter publishes sanitized evidence. Existing tests and application behavior remain authoritative; telemetry is diagnostic evidence with explicit availability and correlation limits.

**Tech Stack:** Existing Vite/Playwright/Node 26, PowerShell, Windows LocalDB; no package additions.

**Spec:** `docs/roadmap/app-00a-build-cache.md`, section “Bổ sung khi review PR #148: quan sát request chậm”.

## Global Constraints

- SYNTHETIC_NONPRODUCTION / CI. User authorizes review, fixes, verification and merge; no new operational acceptance.
- Preserve SDK 10.0.404, Node >=26.13.1 <27, all witnesses, assertion/HTTP/step timeouts, retries 0, validate, concurrency and bootstrap.
- Do not change application C#, business snapshots, identity/tenant authority, transaction/locking semantics, host target or test scenario assertions.
- Preserve the approved Vite blob a18f2ae9caf63cb41c939ad1ea874d0238bd6b5a, package/lockfile, Issue #59 policy/workflow/tests and every negative mutation. No alternate Vite config or plugin path.
- Enable only with FMCG_APP03_READ_DIAGNOSTICS=1 in .4/.5. SQL requires Windows GitHub Actions and exact run/evidence identity.
- Allowlisted telemetry only; no raw SQL, headers, URLs/query values, body, credentials, arbitrary exception messages or other-database metadata.
- Fixed LocalDB tenant-a database, owner/nonce/session/binding verified before sampling; no SQL writes or permission changes.
- Preserve failure results and historic negative evidence. No further unchanged blind reruns; final receipts belong in the existing PR body.

## Review Focus

- Slow/aborted browser requests must retain their original status, body and termination behavior; external-origin and non-GET traffic is outside capture.
- Unexpected URL/error text and environment values must not enter diagnostic records.
- Invalid run/path/ownership must stop observation, never widen the database or file boundary.
- Failed/unavailable/overlong observer must dispose its connection/process without masking test failure; interrupted redaction must leave raw reports outside both upload roots.
- Incomplete/capped samples and overlapping requests must not support claims of no contention or unique request-to-session causality.

### Task 1: HTTP lifecycle observation

**Files:** `web/ci/app03-read-http.mjs`, `web/ci/app03-read-test.mjs`, `web/tests/app03-read-http.test.mjs`; import-only changes in `web/e2e-app03-4/handoff.spec.ts` and `web/e2e-app03-5/field.spec.ts`. Restore `web/vite.config.ts` to the approved bytes and remove the former Vite-only observer declaration/integration test.

**Interfaces:** Consume the opt-in flag and validated APP02C1_WEB_URL; export the original Playwright test object by default and a fixture with BrowserContext listeners when enabled. Emit prefixed JSON for up to 256 exact-origin GET requests/test with fixed route/event labels, local request ID, UTC/elapsed and numeric status. Pending at teardown is not a completed request. APIRequestContext, forwarding and C# probes are outside capture.

- [ ] Add real Chromium/loopback regression cases for successful delivery, cancellation/pending, repeated requests, multiple pages, wrong origin/method/path and sensitive input. Assert original responses, zero opt-out listeners, cleanup after writer failure and absence of secrets in observer output.
- [ ] Implement observation without mutation or raw exception logging; contain diagnostic writer errors, record pending once and remove listeners in finally.
- [ ] Run the focused tests, record results and review the diff independently.

### Task 2: Bounded SQL observation and artifact redaction

**Files:** `scripts/Watch-App03SqlReads.ps1`, `scripts/Get-App03SqlReadFailure.ps1`, `web/ci/app03-sql-setup.mjs`, `web/ci/app03-read-reporter.mjs`, `web/ci/app03-browser-evidence.mjs`, both .4/.5 configs and focused regression files under `web/tests` / `tests/ci`.

**Interfaces:** Consume exact APP02C1_RUN_ID and APP03_EVIDENCE_DIR supplied by the existing harness, plus the CI opt-in. Write NDJSON beneath TestResults/app03-4 or app03-5 for that run. Record SQL request/session IDs, wait type/time/blocker, bounded counters and fixed statement/resource families. No direct HTTP-to-SQL ID mapping is claimed.

- [ ] Test invalid identity/path, child stop/reap, diagnostic failure independence and exact-known-token redaction preserving XML/assertion text and PNG bytes. Test interrupted/failed publication keeps raw reports outside both artifact selections.
- [ ] Start one observer from awaited global setup with a restricted child environment, fixed integrated-auth LocalDB and existing DemoRun ownership predicates. Wait for its own readiness or bounded unavailability (45s maximum); record spawn/readiness/elapsed, stop/reap unsuccessful initialization, and return cleanup for global teardown. Test ready, unavailable and deadline/cleanup behavior without changing scenario budgets.
- [ ] Sample around 250ms with one command at a time, 1s command timeout, 240s deadline, bounded rows/output, parent-stop handling and finally disposal. Include current-database idle blockers and explicitly mark unavailable samples. Find the first SqlException in a wrapped chain; retain bounded continuation for timeout -2 on a usable connection and cover a nested native cause in Windows PowerShell 5.1.
- [ ] Keep list/JUnit reporters; stage raw browser output under the validated runner temporary directory outside upload selections, clean up onEnd, then publish only sanitized known-token text and unchanged screenshot bytes into canonical evidence paths. An interrupted/failed publication must not make raw output uploadable. Do not change returned test status.
- [ ] Parse PowerShell and execute focused platform-independent tests; review the SQL/ownership boundary. Actual LocalDB acceptance remains hosted CI.

### Task 3: Integration and authorized completion

**Files:** `.github/workflows/app00a.yml`, this plan and the owning roadmap; final receipts in PR body.

**Interfaces:** Set the opt-in for exactly APP-03.4/.5. Retain existing TestResults artifact uploads. Consume complete source-bound CI results before merge.

- [ ] Verify original workflow commands/gates and targets are unchanged; run actionlint and existing aggregate-gate tests. Run real Chromium observer tests and the Windows PowerShell 5.1 error-classification regression on CI as part of APP-03 preparation. Compare all approved Vite/security boundary files byte-for-byte and require Security 5/5 on the final HEAD.
- [ ] Independent review of the diagnostic delta, fix material findings and verify the final source tree before publishing.
- [ ] Run full CI once on the substantive new HEAD. Inspect .4/.5 HTTP records and SQL availability, plus all original .NET/SQL/browser/audit gates and reuse calls. If failed, diagnose from the new evidence before choosing any further action.
- [ ] If all final-head checks pass, merge with expected head lease, verify actual merge/tree and all expected push/main workflows. Keep failures, measurements and main receipts in the existing PR; no receipt-only commits.
